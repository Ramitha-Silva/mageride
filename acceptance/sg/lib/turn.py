"""
A TURN client (RFC 5766 / RFC 8656), enough of one to carry RTP through the platform's own relay.

WHY A TURN CLIENT RATHER THAN A WEBRTC CLIENT
---------------------------------------------
C131 has to measure MOS, jitter and packet loss on the media path a Sri Lankan handset actually
uses to talk to a Singapore SFU.  The obvious instrument is a headless WebRTC endpoint, and it is
the wrong one here for two reasons.

The first is that **a WebRTC stack would measure its own jitter buffer.**  NetEq adapts, conceals
and stretches; what comes out of it is a statement about Google's concealment code, not about the
path.  The E-model wants the path's delay, jitter and loss as inputs and models the buffer
separately (`rtpstats.jitter_buffer_ms`), so a probe that hands it post-concealment numbers is
feeding the model its own output.

The second is that **the relay is the thing under test.**  D6' §6 puts coturn on the host UDP
range precisely because it is the path for handsets behind carrier-grade NAT — which the replica
spec calls "the common case on Sri Lankan mobile carriers".  A WebRTC client would connect
peer-to-peer whenever it could and would exercise the relay only by accident; this one allocates
through the relay unconditionally, which is what makes "TURN relay share" a number rather than a
guess.

What this deliberately is NOT: an ICE agent.  It does no candidate gathering, no connectivity
checks and no nomination.  Relay share as measured by this harness is therefore
"what a relayed call costs", and the complementary question — "what fraction of real calls end up
relayed" — is read off coturn's own allocation counters by `collect.sh`, because only the server
can see the calls that never allocated.

THE CREDENTIAL
--------------
`infra/deploy/coturn/turnserver.conf` sets `use-auth-secret`, so this is the ephemeral-credential
scheme: the username is an expiry timestamp and the password is
`base64(HMAC-SHA1(static-auth-secret, username))`.  There are no accounts.  The shared secret is
read from `env.json` (mode 0600, gitignored) and never from a committed file.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import os
import secrets
import socket
import struct
import time
from dataclasses import dataclass

MAGIC_COOKIE = 0x2112A442

# --- methods (RFC 5766 §13) -------------------------------------------------------------------
METHOD_BINDING = 0x001
METHOD_ALLOCATE = 0x003
METHOD_REFRESH = 0x004
METHOD_SEND = 0x006
METHOD_DATA = 0x007
METHOD_CREATE_PERMISSION = 0x008
METHOD_CHANNEL_BIND = 0x009

CLASS_REQUEST = 0x00
CLASS_INDICATION = 0x01
CLASS_SUCCESS = 0x02
CLASS_ERROR = 0x03

# --- attributes -------------------------------------------------------------------------------
ATTR_MAPPED_ADDRESS = 0x0001
ATTR_USERNAME = 0x0006
ATTR_MESSAGE_INTEGRITY = 0x0008
ATTR_ERROR_CODE = 0x0009
ATTR_CHANNEL_NUMBER = 0x000C
ATTR_LIFETIME = 0x000D
ATTR_XOR_PEER_ADDRESS = 0x0012
ATTR_DATA = 0x0013
ATTR_REALM = 0x0014
ATTR_NONCE = 0x0015
ATTR_XOR_RELAYED_ADDRESS = 0x0016
ATTR_REQUESTED_TRANSPORT = 0x0019
ATTR_XOR_MAPPED_ADDRESS = 0x0020
ATTR_SOFTWARE = 0x8022

#: RFC 5766 §11: channel numbers live in 0x4000-0x7FFF.
CHANNEL_MIN = 0x4000
CHANNEL_MAX = 0x7FFE


class TurnError(RuntimeError):
    """A TURN transaction the server refused, carrying the code it refused with."""

    def __init__(self, code: int, reason: str, method: int) -> None:
        super().__init__(f"TURN method 0x{method:03x} refused: {code} {reason}")
        self.code = code
        self.reason = reason
        self.method = method


def message_type(method: int, cls: int) -> int:
    """RFC 5389 §6's interleaved method/class encoding."""
    return (
        ((method & 0xF80) << 2)
        | ((method & 0x70) << 1)
        | (method & 0x0F)
        | ((cls & 0x2) << 7)
        | ((cls & 0x1) << 4)
    )


def _pad4(value: bytes) -> bytes:
    return value + b"\x00" * (-len(value) % 4)


def encode_attribute(kind: int, value: bytes) -> bytes:
    return struct.pack("!HH", kind, len(value)) + _pad4(value)


def parse_attributes(body: bytes) -> list[tuple[int, bytes]]:
    attributes: list[tuple[int, bytes]] = []
    offset = 0

    while offset + 4 <= len(body):
        kind, length = struct.unpack_from("!HH", body, offset)
        offset += 4
        attributes.append((kind, body[offset : offset + length]))
        offset += length + (-length % 4)

    return attributes


def decode_xor_address(value: bytes) -> tuple[str, int]:
    """
    XOR-MAPPED / XOR-PEER / XOR-RELAYED-ADDRESS.

    IPv4 only, and that is a real limit rather than an oversight: coturn's `denied-peer-ip` list
    in `turnserver.conf` covers both families, but the relay range D6' §6 pins (50000-50100) and
    every deployment address in this repository are v4.  A v6-only Sri Lankan carrier would need
    this extended, and `media_probe.py` says so rather than silently mis-parsing.
    """
    family = value[1]

    if family != 0x01:
        raise TurnError(0, f"XOR address family 0x{family:02x} is not IPv4; see decode_xor_address", 0)

    port = struct.unpack_from("!H", value, 2)[0] ^ (MAGIC_COOKIE >> 16)
    address = struct.unpack_from("!I", value, 4)[0] ^ MAGIC_COOKIE

    return socket.inet_ntoa(struct.pack("!I", address)), port


def encode_xor_peer_address(host: str, port: int) -> bytes:
    address = struct.unpack("!I", socket.inet_aton(host))[0] ^ MAGIC_COOKIE

    return struct.pack("!BBHI", 0, 0x01, port ^ (MAGIC_COOKIE >> 16), address)


def ephemeral_credential(secret: str, ttl_seconds: int = 3600, name: str = "c131") -> tuple[str, str]:
    """
    coturn's `use-auth-secret` credential pair.

    `username` is `<unix-expiry>:<name>` and `password` is the base64 of an HMAC-SHA1 of that
    username under the shared secret.  This is the scheme `turnserver.conf` selects, and the
    reason there are no static users on the relay: a credential is a timestamp the platform
    signed, so revoking access is letting it expire.
    """
    username = f"{int(time.time()) + ttl_seconds}:{name}"
    digest = hmac.new(secret.encode(), username.encode(), hashlib.sha1).digest()

    return username, base64.b64encode(digest).decode()


def _integrity_key(username: str, realm: str, password: str) -> bytes:
    """
    RFC 5389 §15.4's long-term credential key: MD5(username:realm:password).

    MD5 is not a choice made here — it is the key derivation the STUN long-term credential
    mechanism specifies, and a server implementing the RFC accepts nothing else.  Marked
    `usedforsecurity=False` so a FIPS build does not refuse it: the digest is a key-derivation
    step inside a protocol, not this harness asserting anything about MD5.
    """
    return hashlib.md5(
        f"{username}:{realm}:{password}".encode(), usedforsecurity=False
    ).digest()


@dataclass
class StunMessage:
    method: int
    cls: int
    transaction: bytes
    attributes: list[tuple[int, bytes]]

    def first(self, kind: int) -> bytes | None:
        for attribute_kind, value in self.attributes:
            if attribute_kind == kind:
                return value
        return None

    @property
    def error(self) -> tuple[int, str] | None:
        value = self.first(ATTR_ERROR_CODE)

        if value is None:
            return None

        code = (value[2] & 0x07) * 100 + value[3]

        return code, value[4:].decode(errors="replace")


def build_message(
    method: int,
    cls: int,
    transaction: bytes,
    attributes: list[tuple[int, bytes]],
    *,
    credential: tuple[str, str, str] | None = None,
) -> bytes:
    """
    Serialises a STUN/TURN message, appending MESSAGE-INTEGRITY when a credential is supplied.

    The integrity attribute is computed over the message with its length field **already
    including the 24 bytes the attribute will occupy** (RFC 5389 §15.4).  Computing it over the
    length as it stands produces a digest every conformant server rejects with 401, which then
    looks exactly like a wrong shared secret — the single most expensive mistake to debug in this
    file, and the reason it is spelled out here.
    """
    body = b"".join(encode_attribute(kind, value) for kind, value in attributes)

    if credential is not None:
        username, realm, password = credential
        header = struct.pack(
            "!HHI12s", message_type(method, cls), len(body) + 24, MAGIC_COOKIE, transaction
        )
        digest = hmac.new(_integrity_key(username, realm, password), header + body, hashlib.sha1).digest()
        body += encode_attribute(ATTR_MESSAGE_INTEGRITY, digest)

    header = struct.pack("!HHI12s", message_type(method, cls), len(body), MAGIC_COOKIE, transaction)

    return header + body


def parse_message(datagram: bytes) -> StunMessage | None:
    """Answers None for anything that is not a STUN message — ChannelData, or noise."""
    if len(datagram) < 20:
        return None

    raw_type, length, cookie = struct.unpack_from("!HHI", datagram, 0)

    if cookie != MAGIC_COOKIE or raw_type & 0xC000:
        return None

    transaction = datagram[8:20]
    method = (raw_type & 0x000F) | ((raw_type & 0x00E0) >> 1) | ((raw_type & 0x3E00) >> 2)
    cls = ((raw_type & 0x0100) >> 7) | ((raw_type & 0x0010) >> 4)

    return StunMessage(method, cls, transaction, parse_attributes(datagram[20 : 20 + length]))


def encode_channel_data(channel: int, payload: bytes) -> bytes:
    """
    RFC 5766 §11.4's ChannelData: a four-byte header and the payload, no STUN framing.

    This is what a real relayed media stream looks like on the wire, and it is why the probe
    binds a channel rather than using Send/Data indications: an indication carries a full STUN
    header plus an XOR-PEER-ADDRESS on every packet, roughly 36 bytes of overhead on a 172-byte
    RTP packet.  Measuring loss and jitter over a framing no call would ever use would be
    measuring the wrong stream.
    """
    return struct.pack("!HH", channel, len(payload)) + payload


def decode_channel_data(datagram: bytes) -> tuple[int, bytes] | None:
    if len(datagram) < 4:
        return None

    channel, length = struct.unpack_from("!HH", datagram, 0)

    if not (CHANNEL_MIN <= channel <= CHANNEL_MAX) or len(datagram) < 4 + length:
        return None

    return channel, datagram[4 : 4 + length]


class TurnAllocation:
    """
    One allocation on the relay: a socket, a relayed transport address, and a bound channel.

    Every request is retried on the 401-with-a-nonce that the long-term credential mechanism
    requires — the first Allocate is *supposed* to fail so the server can hand back its realm and
    nonce, and a client that treats that 401 as a credential failure never allocates at all.
    """

    def __init__(self, server_host: str, server_port: int, secret: str, *, timeout: float = 5.0) -> None:
        self.server = (server_host, server_port)
        self.secret = secret
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.settimeout(timeout)
        self.socket.bind(("0.0.0.0", 0))

        self.username, self.password = ephemeral_credential(secret)
        self.realm: str | None = None
        self.nonce: bytes | None = None
        self.relayed: tuple[str, int] | None = None
        self.mapped: tuple[str, int] | None = None
        self.channel: int | None = None
        self.lifetime: int = 0

    # -- transaction plumbing ------------------------------------------------------------

    def _transact(self, method: int, attributes: list[tuple[int, bytes]], *, authenticate: bool) -> StunMessage:
        for attempt in range(2):
            transaction = secrets.token_bytes(12)
            payload = list(attributes)

            credential = None
            if authenticate and self.realm is not None and self.nonce is not None:
                payload += [
                    (ATTR_USERNAME, self.username.encode()),
                    (ATTR_REALM, self.realm.encode()),
                    (ATTR_NONCE, self.nonce),
                ]
                credential = (self.username, self.realm, self.password)

            self.socket.sendto(
                build_message(method, CLASS_REQUEST, transaction, payload, credential=credential),
                self.server,
            )

            deadline = time.monotonic() + 5.0

            while time.monotonic() < deadline:
                datagram, _ = self.socket.recvfrom(2048)
                message = parse_message(datagram)

                if message is None or message.transaction != transaction:
                    # A relayed data packet arriving mid-handshake, or a stale response.  Not an
                    # error: the allocation is live and the media loop is what reads those.
                    continue

                if message.cls == CLASS_SUCCESS:
                    return message

                error = message.error or (0, "no ERROR-CODE")

                # 401 (Unauthorized) and 438 (Stale Nonce) are the protocol asking us to
                # re-send with the realm and nonce it has just supplied.  Both are normal.
                if error[0] in (401, 438) and attempt == 0:
                    realm = message.first(ATTR_REALM)
                    nonce = message.first(ATTR_NONCE)

                    if realm is not None:
                        self.realm = realm.decode()
                    if nonce is not None:
                        self.nonce = nonce
                    break

                raise TurnError(error[0], error[1], method)

        raise TurnError(0, "no response to a retried request", method)

    # -- the three operations a relayed stream needs -------------------------------------

    def allocate(self, lifetime_seconds: int = 600) -> tuple[str, int]:
        """Allocates a UDP relay and answers its transport address."""
        response = self._transact(
            METHOD_ALLOCATE,
            [
                # RFC 5766 §6.1: 17 is UDP.  The relay range D6' §6 pins is UDP, and coturn's
                # own config sets no `no-udp-relay`, so this is the only transport to ask for.
                (ATTR_REQUESTED_TRANSPORT, struct.pack("!BBBB", 17, 0, 0, 0)),
                (ATTR_LIFETIME, struct.pack("!I", lifetime_seconds)),
            ],
            authenticate=True,
        )

        relayed = response.first(ATTR_XOR_RELAYED_ADDRESS)
        mapped = response.first(ATTR_XOR_MAPPED_ADDRESS)
        lifetime = response.first(ATTR_LIFETIME)

        if relayed is None:
            raise TurnError(0, "allocation succeeded with no XOR-RELAYED-ADDRESS", METHOD_ALLOCATE)

        self.relayed = decode_xor_address(relayed)
        self.mapped = decode_xor_address(mapped) if mapped else None
        self.lifetime = struct.unpack("!I", lifetime)[0] if lifetime else lifetime_seconds

        return self.relayed

    def create_permission(self, peer_host: str, peer_port: int) -> None:
        self._transact(
            METHOD_CREATE_PERMISSION,
            [(ATTR_XOR_PEER_ADDRESS, encode_xor_peer_address(peer_host, peer_port))],
            authenticate=True,
        )

    def bind_channel(self, peer_host: str, peer_port: int, channel: int) -> None:
        self._transact(
            METHOD_CHANNEL_BIND,
            [
                (ATTR_CHANNEL_NUMBER, struct.pack("!HH", channel, 0)),
                (ATTR_XOR_PEER_ADDRESS, encode_xor_peer_address(peer_host, peer_port)),
            ],
            authenticate=True,
        )
        self.channel = channel

    def send(self, payload: bytes) -> None:
        if self.channel is None:
            raise TurnError(0, "send before bind_channel", METHOD_SEND)

        self.socket.sendto(encode_channel_data(self.channel, payload), self.server)

    def close(self) -> None:
        try:
            # Lifetime 0 is RFC 5766's explicit deallocation.  Best effort: a probe that leaked
            # 500 allocations would leave the relay holding them until they expired, and the next
            # run would measure a server that is still busy with the last one.
            self._transact(METHOD_REFRESH, [(ATTR_LIFETIME, struct.pack("!I", 0))], authenticate=True)
        except (TurnError, OSError):
            pass
        finally:
            self.socket.close()


def rtp_packet(sequence: int, timestamp: int, ssrc: int, payload_bytes: int = 160) -> bytes:
    """
    A conformant RTP packet carrying an incompressible payload of `payload_bytes`.

    The payload is random rather than zeros so that nothing on the path — a carrier's
    optimiser, a VPN, the relay itself — can compress it and make the stream look cheaper than a
    voice stream is.  160 bytes is a 20 ms Opus frame at ~64 kbit/s, which with the 12-byte RTP
    header and the 4-byte ChannelData header is what a relayed MageRide call actually puts on the
    wire.

    The 32-bit probe sequence used by the statistics is carried in the payload's first four
    bytes; RTP's own sequence field is 16 bits and wraps every 22 minutes at 50 pps (see
    `rtpstats.StreamAccumulator`).
    """
    header = struct.pack(
        "!BBHII",
        0x80,  # version 2, no padding, no extension, no CSRC
        0x6F,  # dynamic payload type 111 — what WebRTC negotiates for Opus
        sequence & 0xFFFF,
        timestamp & 0xFFFFFFFF,
        ssrc,
    )

    return header + struct.pack("!I", sequence) + os.urandom(max(0, payload_bytes - 4))


def probe_sequence(payload: bytes) -> int | None:
    """Reads back the 32-bit probe sequence `rtp_packet` wrote after the RTP header."""
    if len(payload) < 16:
        return None

    return struct.unpack_from("!I", payload, 12)[0]
