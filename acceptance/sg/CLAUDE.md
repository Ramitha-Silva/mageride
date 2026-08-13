# acceptance/sg/ — conventions for the region-sensitive acceptance suite (C131)

Stack: **python3 + bash**, and nothing else. No .NET, no service code, no k6 — k6 speaks neither
raw UDP with STUN framing nor a binary TCP tracker protocol, which is the whole of what this suite
does. python3 is already a dependency of `load/`, `chaos/` and `infra/replica/`.

**Verify:** `bash acceptance/sg/run.sh --report acceptance/sg/out/report.md`
(after `bash acceptance/sg/configure.sh`, **from a Colombo-side client**, against a deployed
Singapore region).

`README.md` is how to run it, `report.md` is what it found. This file is why it is built this way.

## Rules that are load-bearing

- **The fence is the inverse of every other suite's, and it is structural.** `load/configure.sh`
  and `chaos/run-drills.sh` refuse to run anywhere *but* the replica; this one refuses to record a
  figure *unless* the target is the Singapore region. "Not the replica" is not the same as
  "Singapore" and a declaration in a config file is evidence of nothing, so `lib/region.sh` layers
  a refusal, a declaration and a **physics check**: light in fibre bounds a round trip by the
  great-circle distance, so a Colombo client reaching a target in 40 ms proves it is not in
  Europe. That cannot prove a location — it refutes one, which is the direction that matters.
  **There is deliberately no flag that overrides it.** A run that does not clear the fence is
  titled `NOT EVIDENCE`, carries `evidence: not-evidence` in every payload, and exits 2.

- **`selftest.py` is a gate, not a convenience, and it runs first.** This harness executes against
  the region once; that run is expensive to arrange and produces the numbers a go-live decision is
  made on. It is also the first time most of this code meets anything real — the exact shape of
  C130's finding about `infra/replica/restore.sh`, a recovery script that could never recover
  because its own verification never exercised the broken path. So every calculation is pinned
  against something true independently of this repository: G.107's published values, RFC 3550's
  estimator on a stream whose jitter is known by construction, RFC 5389's own worked message-type
  values, and GT06's documented login acknowledgement `78 78 05 01 00 01 D9 DC 0D 0A`.
  `run.sh` exits **3** if it fails and attempts nothing.

- **A TURN client, not a WebRTC endpoint.** A WebRTC stack would measure its own jitter buffer —
  NetEq adapts, conceals and stretches, and the E-model wants the path's delay, jitter and loss as
  *inputs* while modelling the buffer separately, so feeding it post-concealment numbers is
  feeding the model its own output. And a WebRTC client connects peer-to-peer whenever it can, so
  it would exercise the relay only by accident. `lib/turn.py` allocates unconditionally, binds a
  channel (not Send/Data indications — an indication adds ~36 bytes of STUN framing to a 172-byte
  RTP packet, and measuring loss over a framing no call uses is measuring the wrong stream), and
  streams a conformant 20 ms Opus-shaped RTP flow.

- **The probe's echo path is TWO traversals of the call's one-way path, so the network term is
  half the measured RTT.** `A → relay → B → relay → A`. The halving is structural rather than the
  usual symmetry assumption — the same hops in opposite directions, each with one relay traversal.
  Getting it wrong in either direction moves the Colombo-TURN recommendation across its own
  threshold, so it lives in one function (`rtpstats.relayed_call_delay_ms`), is argued in that
  module's docstring, and is pinned by the self-test.

- **MOS is computed and labelled, never asked.** **No document in `specs/` gives a MOS floor, a
  jitter budget or a packet-loss budget** — those words appear in this component's prompt and in
  no specification. So the figure is ITU-T G.107, whose inputs are measurable and whose parameters
  are published, and every approximation is carried into the output rather than buried: G.113 has
  no Opus row so the impairment parameters are G.711+PLC's (`CODEC_NOTE` travels with every
  result), and the advantage factor is **0** rather than G.107 §B.2's 10 for "mobile in a moving
  vehicle" — which is literally every call here. `A` is the one term that exists to excuse a bad
  connection, and a harness that grants itself ten points of `R` before it starts is not
  measuring.

- **Every figure is labelled region-specific or transferable, in the JSON and not only in prose.**
  Media figures and tracker RTTs describe one Colombo↔Singapore path and mean nothing about any
  other pair; the AL-48 fallback result is a contract property and is true everywhere. They must
  not appear in one table without the label, which is C131's fourth definition-of-done item.

- **Absence is asserted on the raw text, never on a deserialised shape.** C122's rule. "The
  response carries no masked relay" is a claim about what is *not* there, and a closed DTO says
  nothing about a member its type has no property for. `fallback_probe.py` sweeps every response
  body as text for the vocabulary AL-48 removed and for anything shaped like a Sri Lankan MSISDN.
  voip-svc's own `MaskingWithdrawnTests` covers the other half — that no *identifier* in the
  assembly is named after that stack.

- **The client's view is never the only view.** `collect.sh` reads coturn's and LiveKit's own
  counters, because the one number the probe structurally cannot produce is the **relay share**:
  this suite relays unconditionally, so it measures what a relayed call costs and can say nothing
  about how many calls are relayed. C129's central finding is exactly a case where the two views
  disagreed.

- **Where a limit could not be reached, the report says so.** `chaos/CLAUDE.md`'s rule. The
  probe's send loop is single-threaded and enters its own tail at high concurrency, so the 500-call
  target must be spread across several Colombo-side clients. That is stated in `report.md` §4
  rather than discovered mid-run.

- **Nothing here holds a credential in a committed file.** `env.json` carries the TURN shared
  secret, a live bearer and the MQTT credential; it is written at 0600 by `configure.sh` and is
  gitignored. Bearers are obtained through `POST /v1/auth/otp/request` + `verify`, never minted —
  iam-svc's RS256 key is not something an acceptance harness may hold.

## Traps this suite has already fallen into

- **A missing bind-mount source becomes a DIRECTORY.** Verified on this box: Compose creates the
  path and the container sees `/etc/livekit.yaml` as `directory`, so the process cannot read its
  configuration and the failure is nothing to do with the configuration's contents. This is what
  `infra/replica/docker-compose.light-replica.yml`'s `voip` profile does today (finding C131-05).

- **coturn refuses a permission for a loopback or private peer, with `403 Forbidden IP`.** The
  first rehearsal allocated fine and then failed at `CreatePermission`, because both relayed
  addresses were on `127.0.0.1`. A rehearsal has to give coturn a routable `relay-ip`, or it
  measures its own denied-peer list.

- **The relay port range is a hard concurrency ceiling and it arrives without warning.** The 51st
  call against a 101-port range is refused `508 Cannot create socket` while the 50 already
  allocated carry on at 0 % loss. A sweep that only watched quality would have seen a perfectly
  healthy relay and missed the ceiling entirely (finding C131-02).

- **MESSAGE-INTEGRITY is computed over a length that already counts its own 24 bytes** (RFC 5389
  §15.4). Computing it over the length as it stands produces a digest every conformant server
  answers `401` to — which looks exactly like a wrong shared secret, and is the most expensive
  mistake available in `lib/turn.py`. Pinned by the self-test against a hand-computed HMAC.

- **The first Allocate is *supposed* to fail.** The long-term credential mechanism answers `401`
  with the realm and nonce the client must echo; a client treating that as a credential failure
  never allocates at all. `438 Stale Nonce` is the same conversation later.

- **RTP's sequence field is 16 bits and wraps every 22 minutes at 50 pps.** The statistics key on
  a 32-bit counter carried in the payload; the header still writes a conformant 16-bit sequence.

- **Packets in flight when the window closes are not losses.** C130's flood drill reported the
  platform failing R-09 from its own shutdown discarding five in-flight acknowledgements. The
  media probe stops sending, drains for two seconds, and only then counts.

## Not here, on purpose

- **ICE.** No candidate gathering, no connectivity checks, no nomination. "What fraction of real
  calls end up relayed" is `collect.sh`'s question and only the server can answer it.
- **The SFU's own media path.** This suite measures the relay. LiveKit's media plane needs a real
  WebRTC client, and the reasons above are why there is not one here.
- **A probe that publishes `veh/+/cmd` as a matter of course.** That topic is a platform service's
  to produce, and standing in for a component (rather than for the outside world) is the line
  `tests/E2E`'s fence draws. `--downlink broker` does it because otherwise the deliverable is
  unmeasurable at all (finding C131-06), it is behind an explicit flag, and every figure it
  produces is stamped `leg: broker-to-device`.
- **Any change to a service, spec, contract or migration.** This component measures and deploys
  the media plane it measures. All seven findings are recorded with an owner in `report.md`; none
  is fixed in the dev or replica configuration. `infra/sg/` is the corrected media plane for the
  region under test, not a patch to C055's or C125's files.
