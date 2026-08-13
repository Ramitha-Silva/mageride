# C131 — VoIP and tracker acceptance, Singapore region

**Status: the acceptance run has not happened, and this report is not it.**

The Singapore region does not exist yet. Production is DigitalOcean Kubernetes in Singapore
(hosting decision 2026-07-05, D7' §8) and **C132 is the component that builds it** — C132 depends
on C131, so at the point this component runs there is no cluster, no media host, no Colombo-side
client and no bound hardware tracker in Sri Lanka. `bash acceptance/sg/run.sh` exits **2** naming
each missing thing, which is `infra/replica/gtfs-day0-verify.sh`'s shape and C126's precedent: a
component blocked on something outside the repository states the blockage in its exit code rather
than in a paragraph a reader can skip.

What this component **did** produce is in three parts:

1. **The harness**, complete and self-tested — `acceptance/sg/`. 100 instrument checks pass
   against ITU-T G.107's own published values, RFC 3550, RFC 5389/5766 and D6' §4.1's frame
   layouts. It was rehearsed end to end against a real coturn and measured real streams.
2. **The Singapore media plane**, as deployable artefacts — `infra/sg/`.
3. **Seven findings**, six of them defects in the media plane as it stands today. Two are
   demonstrated rather than argued, and **the headline one means the TURN relay share this
   component was asked to measure could only ever have read zero — and not because relaying was
   unnecessary.**

---

## 1. What could not be measured, and why

| Definition-of-done item | Status | Why |
|---|---|---|
| VoIP concurrency and quality targets measured in-region | **not measured** | no Singapore region; no media host; no Colombo client |
| tracker RTT measured from Sri Lankan clients, ingest in Singapore | **not measured** | same, plus no bound GT06/JT808 device in Sri Lanka |
| the direct-dial fallback verified on a forced VoIP failure | **harness built, not driven** | the probe exists and asserts both halves of the contract; forcing the failure needs a deployment to force it on |
| the report states which figures are region-specific | **done** | §6 below, and `run.sh` writes the same table into every report it generates |

The build host is the Contabo VPS in Germany. **EU numbers are not acceptance evidence** — C131's
first fence — so nothing was run against the replica and reported as though it were a region. The
one exception is explicitly labelled: §4's rehearsal, run against a coturn on this box, which
exists to prove the instrument works and produces no acceptance figure.

### The fence is structural, not a convention

`acceptance/sg/lib/region.sh` is consulted before any probe runs, and there is deliberately no flag
that overrides it. Three layers, none of which is a self-declaration on its own:

1. **Refusal** — the target must not be a loopback, a private address, or this box's own public
   address while the replica is running on it.
2. **Declaration** — `env.json` must carry `region: sgp` and a client location.
3. **Physics** — light in fibre moves at ~200,000 km/s, so a round trip has a floor set by the
   great-circle distance. Colombo→Singapore is ~2,880 km (**≥28.8 ms RTT**); Colombo→Frankfurt is
   ~7,900 km (**≥79 ms**). A target answering a Colombo client in 40 ms **cannot** be in Europe;
   one taking 180 ms is not in Singapore. This cannot prove a location — it refutes one, which is
   the direction that matters.

A run that does not clear all three produces no acceptance figure at all: the report is titled
`NOT EVIDENCE`, every JSON payload carries `evidence: not-evidence`, and the exit code is 2.

---

## 2. Findings

Every one of these is region-independent — they are properties of the configuration, findable
without a Singapore region, and each would have degraded or invalidated the acceptance run itself.

### C131-01 (HIGH, owner C132) — the SFU never tells any client that the relay exists

`infra/deploy/livekit/livekit.yaml` sets `turn.enabled: false` and declares **no
`rtc.turn_servers` block**. `voip-svc`'s token response is
`VoipTokenResponse(RoomName, Token, WsUrl, Callee)` — it carries no ICE servers either. LiveKit's
documented behaviour with no TURN configured and no STUN configured is to fall back to **Google's
public STUN servers**; there is no path by which a handset learns that coturn is there.

So coturn is deployed, hardened with a full denied-peer list, given a shared-secret credential
scheme and a host UDP range — and is offered to nobody. Every ICE negotiation gets STUN and no
relay candidate.

The failure is not an error. It is **a call that rings and then has no audio**, on exactly the
handsets `turnserver.conf`'s own header was written for: *"the path for the handsets behind a
symmetric NAT, which on Sri Lankan mobile carriers is a large minority of them, and a call that
cannot relay is a call that rings and then has no audio."*
`specs/lightweight-production-replica.md` puts it more strongly still — carrier-grade NAT is
*"the common case on Sri Lankan mobile carriers"*.

**And it makes C131's own headline metric meaningless.** "TURN relay share at target concurrency"
would have measured 0 % on this build, and 0 % would have been read as *"peer-to-peer is working,
we do not need a Colombo relay"* — the exact opposite of what it means. A relay share of zero and
a relay nothing can reach are indistinguishable from the client side, which is why
`collect.sh` reads the SFU's own declaration from the deployed file and says so in the output.

Fixed in `infra/sg/livekit.sg.yaml`, which declares both the UDP and the TLS relay with
`secret_file` so LiveKit mints the ephemeral credential per participant.

### C131-02 (HIGH, owner C132) — the relay range is 101 ports against a 500-call target

D6' §6 pins the TURN range at `50000–50100`. That is **101 ports**, and one allocation consumes
one port. A call in which both parties are relayed — the CGNAT case, the common one — consumes
two:

```
101 ports ÷ 2 allocations per call = 50 concurrent relayed calls
```

against **500 concurrent calls** (ADD §3.2 "In-app VoIP concurrent calls (Phase 1)", and D-24's
"P1 target 500 concurrent calls"). A **10× shortfall**.

**Demonstrated, not derived.** Against a coturn with a 101-port range, this component drove 60
calls: 50 established and the 51st was refused

```
TurnError: TURN method 0x003 refused: 508 Cannot create socket
```

with the 50 already allocated carrying on unaffected — 0 % loss, 399/399 packets returned each.
The ceiling is hard, it arrives without warning, and it arrives at a tenth of the target.

The one merciful thing: a refused *allocation* means the client cannot connect the call, which
puts up AL-48's "Call normally instead?" rather than connecting a call with no audio. The failure
degrades to a direct dial instead of to silence.

`livekit.yaml`'s own comment noticed the range binds — *"the ceiling that actually binds is file
descriptors and the UDP port range above (100 ports); this is a guard rail, not the capacity
plan"* — and did not do the arithmetic against 500. **No document in `specs/` reconciles the
range with the concurrency target.** Widening it (`infra/sg/turnserver.sg.conf` uses
`50000–51200`, 600 relayed calls) is a deviation from D6' §6's stated range and is recorded here
as one.

### C131-03 (MEDIUM, owner C132) — coturn's TLS listener has no certificate and has never listened

`turnserver.conf` sets `tls-listening-port=5349` and comments *"TLS on 5349 for networks that
block plain STUN/TURN. The certificate is the platform's, mounted from infra/deploy/certs."*
There is **no `cert=` or `pkey=` directive in the file** and no certificate mounted into the
container by any compose file. Running the deployed configuration verbatim:

```
WARNING: cannot find certificate file: turn_server_cert.pem (1)
WARNING: cannot start TLS and DTLS listeners because certificate file is not set properly
WARNING: cannot find private key file: turn_server_pkey.pem (1)
```

So 5349 has never listened, in any environment. The fallback the file exists to provide — for the
carriers that block plain UDP — is absent, and coturn starts successfully and says so once at
INFO level. `infra/docker-compose.dev.yml` does not publish 5349 either.

### C131-04 (MEDIUM, owner C132) — `TURN_SECRET` is set nowhere, so `use-auth-secret` has nothing to verify

`turnserver.conf` selects the ephemeral-credential scheme (`use-auth-secret`) and its comment says
the secret *"is supplied by the environment (TURN_SECRET)"*. `TURN_SECRET` appears in **no compose
file, no environment file and no Kubernetes overlay** — the only occurrence in the repository is
that comment. coturn starts anyway and logs nothing about it.

Related, and the same shape as C127's dead-configuration findings: **`Turn__Realm` binds nothing.**
It is in `infra/env/.env.app.example` (`Turn__Realm=mageride.lk`), it is checked by
`infra/scripts/slim-verify.sh`'s D7' §4.2 list, and `VoipOptions` has **no `Turn` section at all**.
It is also not in `service-catalog.yaml`'s `unwiredSecrets`, where `LiveKit__ApiKey` and
`LiveKit__Secret` are correctly recorded as superseded. Its value disagrees with the realm coturn
actually uses (`voip.mageride.lk`).

### C131-05 (MEDIUM, owner C125/C132) — the replica's `voip` profile cannot start, and has no coturn in it

Three things, in one compose service:

1. It bind-mounts `./livekit.replica.yaml`, and **that file does not exist in the repository.**
   Docker Compose creates a missing bind source as a **directory** — verified on this box: the
   container sees `/etc/livekit.yaml` as `directory`. LiveKit cannot read its configuration.
2. **There is no coturn container.** The spec's own entry calls this container
   *"LiveKit SFU + coturn (multi-service compose)"*, the compose comment says
   *"voip: LiveKit SFU + coturn"*, and it publishes `3478/udp` and `50000-50100/udp` — to a
   service running `livekit/livekit-server` with `turn.enabled: false`. Nothing listens on 3478.
3. It publishes the relay range **through docker-proxy on a bridge network**, which is precisely
   what `docs/runbooks/voip-call-setup.md` §"What not to do" forbids: *"Do not publish the RTC
   port range through docker-proxy. A hundred UDP ports through a userland hop, on a media path."*
   The dev compose gets this right with `network_mode: host`; the replica does not.

Not patched from here — the replica's compose is C125's and the media plane's production shape is
C132's. `infra/sg/docker-compose.media-sg.yml` is the corrected shape.

### C131-06 (MEDIUM, owner C132) — the downlink command plane has no producer in any deployment

This one directly blocks a C131 deliverable ("downlink command latency").

`tcp-adapter` subscribes `veh/+/cmd`, translates all five commands and counts deliveries
(`DownlinkRouter`, `Adapter:DownlinkEnabled` defaults **on**). The only service in the entire
platform that publishes to that topic is `trip-state-svc`'s `CadencePublisher`, which pushes a
`setPosRate` hint on a Mode A/B session transition (D5' §5.2, R-07). It is behind
`TripState:PublishCadenceHints`, which **defaults to `false` and appears in no environment file,
no compose file and no Kubernetes overlay** — the same shape as C130's finding about
`Dispatch__LastWillEnabled`.

`pingNow`, `reboot` and `setGeofence` have **no producer at all**. (`revokeCredential` is honoured
by closing the socket through T-12's separate path, so its absence here is by design.)

So the end-to-end downlink latency is unmeasurable on any deployment as configured.
`tracker/rtt_probe.py` therefore offers two paths and labels them differently: `--downlink
platform` measures the whole path and needs the flag turned on; `--downlink broker` publishes the
envelope directly and every figure it produces is stamped `leg: broker-to-device`, because it
omits the API hop and the service that would have decided to send the command.

### C131-07 (LOW, planner) — C131 and C132 both own the Singapore media plane

`infra/k8s/service-catalog.yaml` and `infra/k8s/overlays/production/kustomization.yaml` both
record that **C132** owns *"LiveKit+coturn pinned SGP"*. C131's first deliverable is *"LiveKit +
coturn deployment in Singapore with Colombo-side test clients"*. C132 depends on C131, so the
component that must measure the media plane is scheduled before the component that owns deploying
it.

Resolved here by building the media plane as deployable artefacts under `infra/sg/` — the thing
under test, reproducible from the repository — and leaving the production topology decision, the
host provisioning and the cutover to C132. `deploy-media-sg.sh` stops short of both the
provisioning and the `voip-svc` re-pointing, and prints them as next steps.

---

## 3. The instrument, and why it is shaped this way

**A TURN client, not a WebRTC endpoint.** Two reasons. A WebRTC stack would measure *its own*
jitter buffer — NetEq adapts, conceals and stretches, and what comes out is a statement about
Google's concealment code rather than about the path, while the E-model wants the path's delay,
jitter and loss as *inputs* and models the buffer separately. And a WebRTC client connects
peer-to-peer whenever it can, so it would exercise the relay only by accident. This one allocates
through the relay unconditionally.

**MOS is computed, not asked.** No specification in `specs/` gives a MOS floor, a jitter budget or
a packet-loss budget — the words appear in this component's prompt and nowhere in the specs. So
the figure is ITU-T G.107's E-model, whose inputs are measurable and whose parameters are
published. Two approximations are carried into every report rather than buried:

- **The codec is Opus and the impairment parameters are G.711's** (`Ie=0`, `Bpl=25.1`, G.113
  App. I). G.113 has no Opus row. Opus at 64 kbit/s wideband is rated at or above G.711
  narrowband, so this is conservative on the codec axis.
- **The advantage factor `A` is 0**, not G.107 §B.2's 10 for "mobile in a moving vehicle" — which
  is literally every call on this platform. `A` is the one term that exists to *excuse* a bad
  connection, and a harness that grants itself ten points of `R` before it starts is not
  measuring. Both are reported.

**The delay arithmetic, which the whole Colombo-TURN question turns on.** Write `L` for the
one-way Colombo↔Singapore latency.

- A call between two Colombo handsets relayed through Singapore goes Colombo → Singapore →
  Colombo. Its one-way, mouth-to-ear **network** delay is `2L` — a full Singapore round trip of
  geography, for a call between two people in the same city.
- The probe's echo path is `A → relay → B → relay → A`, which is `4L`: exactly **two traversals**
  of the call's one-way path. So `one-way call delay = probe RTT ÷ 2`.

The halving is structural rather than the usual symmetry assumption — the two halves are the same
hops walked in opposite directions, each containing one relay traversal. What it still cannot see
is routing asymmetry, which is genuinely unmeasurable from one clock and is a stated limit rather
than something corrected for. Getting this factor wrong in either direction moves the Colombo-TURN
recommendation across its own threshold, which is why it lives in one function
(`rtpstats.relayed_call_delay_ms`) and is pinned by the self-test.

**GT06 and JT/T 808 measure different frames, and that is the protocols' doing.** GT06
acknowledges login, status and alarm — **never a location frame**: *"the protocol does not ask for
one and some firmware drops the session on an unexpected reply"* (`Gt06Codec`). JT/T 808 answers a
platform general response `0x8001` to the location report `0x0200` itself. So GT06 measures
session establishment and the steady-state heartbeat; JT/T 808 measures a **position report's own
round trip**, which is the closer reading of this component's words. Both are reported separately,
because averaging a heartbeat round trip with a position round trip produces a number describing
neither.

---

## 4. The rehearsal — what was actually run, and what it is not

Run on the Contabo EU build host against a coturn started from `infra/sg/turnserver.sg.conf`'s
shape. **None of it is acceptance evidence**; it exists to prove the instrument works before the
one expensive in-region run, which is C130's lesson about `infra/replica/restore.sh` — a recovery
script that could never recover, whose own verification never exercised the broken path.

| What | Result |
|---|---|
| `selftest.py` | **100 checks passed** — G.107 reference values, RFC 3550 jitter and burst ratio, RFC 5389 message types and MESSAGE-INTEGRITY, and GT06's documented login acknowledgement reproduced byte for byte |
| 5 relayed calls, 8 s, 50 pps | 5/5 established, **399/399 packets returned per call**, 0 % loss, jitter 6.8 ms, MOS-CQE 4.409 |
| 60 relayed calls against a 101-port relay | **50 established, the 51st refused `508 Cannot create socket`** — C131-02, demonstrated |
| `run.sh --report …` with no region | **exit 2**, naming all five missing things |

The MOS of 4.409 is the unimpaired ceiling and is the correct answer for a same-box path: it is
what G.107's default connection scores, and it confirms the model bottoms out where it should
rather than telling anybody anything about Singapore.

**One limit of the instrument, found by running it.** The probe's send loop is single-threaded, so
at higher concurrency its own scheduling enters the tail: at 5 calls the p50 RTT was 4.7 ms and
the p95 58 ms on a loopback path where both should be sub-millisecond. The p50 is trustworthy and
the tail at high concurrency is partly the probe's. **The in-region run must therefore spread the
concurrency target across several Colombo-side clients rather than driving 500 calls from one**,
and should record the per-client count. This is the same class of limit `chaos/CLAUDE.md` records
about its own storm generator, and it is stated here rather than discovered mid-run.

---

## 5. The Colombo TURN recommendation

**Recommendation: provision a TURN relay in Colombo, and treat it as a launch requirement rather
than a pilot question — but decide it on the measurement below, which the in-region run produces.**

The reasoning is arithmetic once the model is fixed. A relayed call between two Colombo handsets
carries `2L` of network delay where `L` is the one-way Colombo↔Singapore latency. Feeding the
E-model with zero loss and the harness's own jitter-buffer and codec terms
(`jitter_buffer_ms(0) = 20 ms`, `codec = 25 ms`), the delay budget at each MOS floor is:

| MOS-CQE floor | Total one-way budget `Ta` | Leaves for network (`2L`) | Implied max `L` | Implied max Colombo↔SGP RTT |
|---|---|---|---|---|
| 4.40 | 162.6 ms | 117.6 ms | 58.8 ms | **117.6 ms** |
| 4.30 | 215.4 ms | 170.4 ms | 85.2 ms | **170.4 ms** |
| 4.20 | 243.7 ms | 198.7 ms | 99.3 ms | **198.7 ms** |
| 4.00 | 291.6 ms | 246.6 ms | 123.3 ms | **246.6 ms** |

The last two columns are the same number because the relay doubles the geography: the one-way
call delay is `2L`, and the Colombo↔Singapore round trip is also `2L`. Reproduce the table with:

```bash
python3 -c "import sys; sys.path.insert(0,'acceptance/sg/lib'); import emodel, rtpstats
fixed = rtpstats.jitter_buffer_ms(0.0) + rtpstats.CODEC_DELAY_MS
for f in (4.4, 4.3, 4.2, 4.0):
    ta = emodel.delay_budget_ms(f); print(f, round(ta,1), round(ta-fixed,1))"
```

So on delay alone, a Singapore relay is comfortable: a Colombo↔Singapore RTT would have to exceed
**~170 ms** before a relayed call drops below MOS 4.3, against a great-circle floor of 28.8 ms and
a realistic path of well under half that budget. **Geography is not the argument for a Colombo
relay**, and this component declines to make it one.

**The argument is C131-02 and the failure mode, not the delay.** At 101 relay ports the Singapore
relay tops out at 50 concurrent relayed calls; splitting the relay pool between Colombo and
Singapore doubles the ceiling and puts the nearer half's media on a domestic path. And a relay in
the same country as both parties removes `2L` entirely, which buys back the whole delay budget for
the loss and jitter terms — which are the ones a mobile network actually moves.

**What the in-region run must produce to close this.** Three numbers, all of which the harness
already emits:

1. `one_way_network_ms_mean` from `voip-media.json` — the `2L` term, measured.
2. `loss_percent_mean` and `jitter_ms_mean` at the target concurrency — if these are the binding
   terms rather than delay, a Colombo relay helps more than the table above suggests.
3. `relay_share` from `server-side.json` — **and this one is worthless until C131-01 is fixed**,
   because a share of zero currently means "no client was ever offered a relay", not "no call
   needed one".

---

## 6. Which figures are region-specific, and which transfer

C131's fourth definition-of-done item, and it applies to every future run of this harness.

**Region-specific and non-transferable — these describe one Colombo↔Singapore path and nothing
else:**

- every VoIP media figure: MOS-CQE, jitter, packet loss, burst ratio, RTT and the
  `network_one_way_ms` term of the delay budget;
- every tracker RTT: GT06 login and status, JT/T 808 position;
- downlink command latency on the `end-to-end` leg;
- the TURN relay share, which is a property of the carriers the clients are on.

Re-running any of these from a different origin measures a different path. They must not be quoted
without both endpoints named, which is why `run.sh` writes the declared client location into every
report and `env.json` refuses to omit it.

**Transferable — properties of the platform's contract, not of the region:**

- the AL-48 direct-dial fallback result. Whether `/v1/voip/token` answers 503 with LiveKit
  unreachable, whether `direct_dial` is recorded anyway, whether an outcome can be reported once
  and not overwritten, and whether any response carries the withdrawn masking vocabulary or a
  phone number — all are true or false everywhere. `voip-fallback-*.json` carries
  `region_sensitive: false` for this reason.
- every finding in §2. They are configuration facts.

**Modelled, not measured — stated at their own term in every JSON payload:**

- the jitter-buffer contribution to `Ta` (it lives inside the handset's WebRTC stack and is not on
  the wire);
- the codec impairment parameters (G.711+PLC standing in for Opus).

---

## 7. What the next session needs

1. A **Singapore media host** (DigitalOcean SGP1, beside the DOKS cluster) — `MEDIA_HOST`,
   then `bash infra/sg/deploy-media-sg.sh --dry-run`.
2. A **Colombo-side client** with `python3`, running `configure.sh` and `run.sh` **from Colombo**.
   Several of them for the 500-call target — see §4's note on the probe's own tail.
3. **Two bound trackers** in Sri Lanka, GT06 and JT/T 808, with their IMEIs in
   `prov.tracker_bindings` for the Singapore deployment. `tcp-adapter` publishes nothing before
   the vehicle is known, and an unbound IMEI is refused silently from the device's side.
4. **C131-01 fixed first.** Measuring relay share against a build where no client is told the
   relay exists produces a confident zero and the wrong conclusion.
5. `TripState__PublishCadenceHints=true` if the end-to-end downlink number is wanted (C131-06).
