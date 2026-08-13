# acceptance/sg — VoIP and tracker acceptance, Singapore region (C131)

Region-sensitive acceptance runs that are meaningless anywhere else: VoIP call quality and
hardware-tracker round-trip time, measured from Sri Lanka against the Singapore region.

**`report.md` is what this component found. Read it first** — the acceptance run has not happened
(there is no Singapore region until C132 builds it), and seven findings in the media plane are
recorded there, two of them demonstrated.

## Running it

```bash
# 1. the instruments — no network, no region, ~1 s
python3 acceptance/sg/selftest.py

# 2. the media plane, onto a host in Singapore
MEDIA_HOST=203.0.113.10 LIVEKIT_API_KEY=… LIVEKIT_API_SECRET=… TURN_SHARED_SECRET=… \
  bash infra/sg/deploy-media-sg.sh --dry-run     # then without --dry-run

# 3. FROM A COLOMBO-SIDE CLIENT
TURN_SHARED_SECRET=… C131_GT06_IMEI=… C131_JT808_IMEI=… \
  bash acceptance/sg/configure.sh --region sgp --client colombo \
    --media-host 203.0.113.10 --tracker-host 203.0.113.11 \
    --platform https://api.mageride.lk

# 4. the verify command
bash acceptance/sg/run.sh --report acceptance/sg/out/report.md
```

Step 3 must run **from Colombo**. Every figure this suite produces is about a path, and the path
starts wherever `configure.sh` ran — which is why `--client` is required, is written into
`env.json`, and appears beside every RTT in the report.

## What the exit code means

| Code | Meaning |
|---|---|
| **0** | every acceptance figure the definition of done names was measured in-region |
| **1** | the run happened and something missed its target. The numbers stand — a failing threshold is a result, never a reason to soften a threshold |
| **2** | the run could **not** happen: the region, the Colombo clients or the devices are not there. Nothing was measured and nothing is reported as measured |
| **3** | the instruments are unsound (`selftest.py` failed). Nothing was attempted |

Exit 2 is the current state on any host in this repository, and `run.sh` prints each missing thing
by name. That is `infra/replica/gtfs-day0-verify.sh`'s shape and C126's precedent.

## The fence

**These runs happen in the Singapore region, not on the Contabo EU replica. EU numbers are not
acceptance evidence.** `lib/region.sh` enforces it in three layers — a refusal, a declaration, and
a light-speed check that can refute a claimed location — and there is no flag that overrides it. A
run that does not clear the fence is titled `NOT EVIDENCE` and exits 2.

For working on the harness itself there is `--rehearse`, which skips the fence, stamps everything
`rehearsal`, and still exits 2. It exists so the one in-region run is not the first time this code
executes.

## Layout

```
run.sh              the verify command — self-test, fence, probes, report
configure.sh        writes env.json (0600, gitignored). Run it from Colombo
collect.sh          the server's own view — the relay share lives here, not in the probe
selftest.py         100 checks. G.107, RFC 3550, RFC 5389/5766, D6' §4.1. A gate on everything
lib/emodel.py       ITU-T G.107 E-model, and the delay budget the Colombo-TURN answer comes from
lib/rtpstats.py     RFC 3550 jitter and loss, and the one-way delay arithmetic
lib/turn.py         a TURN client (RFC 5766) — allocate, permission, channel, RTP
lib/frames.py       GT06 and JT/T 808 frames, from D6' §4.1's layouts
lib/region.sh       the fence
voip/media_probe.py       MOS, jitter, loss and concurrency through the relay
voip/fallback_probe.py    AL-48 — the direct-dial fallback, and the masking that must not return
tracker/rtt_probe.py      GT06 and JT/T 808 round trips, and the downlink
```

The media plane it measures is `infra/sg/` — `livekit.sg.yaml`, `turnserver.sg.conf`,
`docker-compose.media-sg.yml` and `deploy-media-sg.sh`.

## Forcing the failure

The third definition-of-done item is *"the direct-dial fallback is verified on a forced VoIP
failure"*, and forcing it needs a deployment to force it on:

```bash
# in-region, on the media host
docker compose -f /opt/mageride-media/docker-compose.media-sg.yml stop livekit

# then, from the Colombo client
python3 acceptance/sg/voip/fallback_probe.py --env acceptance/sg/env.json --expect unavailable
```

Set `voip.forcedFailureArranged` to `true` in `env.json` once it genuinely has been. `run.sh`
records a blocker until it is, because a fallback nobody forced is a fallback nobody verified.

## Reading the numbers

- **Region-specific and non-transferable**: every MOS, jitter, loss and RTT figure, and the
  end-to-end downlink latency. They describe one Colombo↔Singapore path.
- **Transferable**: the AL-48 fallback result — a contract property of voip-svc, marked
  `region_sensitive: false` in its own payload.
- **Modelled, not measured**: the jitter-buffer term inside every MOS (it lives in the handset's
  WebRTC stack and is not on the wire) and the codec impairment parameters (G.711+PLC; G.113
  publishes no Opus row). Both are printed at their own term so a reader can substitute their own.

`report.md` §6 is the full table, and `run.sh` writes a copy of it into every report it generates.
