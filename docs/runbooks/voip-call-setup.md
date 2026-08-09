# Runbook — VoIP call setup slow (ADD §13.3 row 4)

**Alert:** `VoipCallSetupSlow` · **Severity:** ticket
**Dashboard:** Grafana → `mageride-slo`

> ADD §13.3 row 4: signalling → first audio frame, **p95 < 4 s**, 99% monthly, "when Phase 1 voip-svc
> shipped".

**Not instrumented.** voip-svc (C055) exists; the histogram does not, because *the first audio frame
is observed by the SFU and not by the service*. LiveKit knows when media starts flowing; voip-svc
only knows when it minted a token. Closing that gap needs LiveKit's webhook or its own metrics
scraped into Prometheus — recorded in the C119 handoff.

---

## First action

**Check whether calls are connecting at all**, which is observable now:

```bash
docker compose -f infra/docker-compose.dev.yml logs --tail 100 livekit | grep -iE "room|participant|ice"
curl -s http://127.0.0.1:7880/ >/dev/null && echo "livekit signalling up"
```

---

## The three legs, and which one is usually at fault

| Leg | What it is | Symptom when slow |
|---|---|---|
| Signalling | WebSocket to LiveKit on 7880, proxied by HAProxy | Nothing happens for seconds after tapping call |
| ICE | Candidate gathering and connectivity checks | Both sides "connected" and no audio for a beat |
| Media | RTP, direct or through coturn | One-way audio, or audio that starts late |

**ICE falling back to the TURN relay is the usual cause of a slow setup.** Direct peer-to-peer
connects in well under a second; a relayed path adds a round of candidate checks plus the relay hop.

---

## Diagnose

1. **Is coturn reachable?** Both `livekit` and `coturn` run on **host networking**, and D6' §6 is
   explicit about why: "TURN media relay (coturn) on host UDP range (3478 + 50000-50100), **NOT** via
   HAProxy/L7 (HAProxy cannot relay UDP)". If either has been moved onto the bridge network, media
   breaks in ways that do not look like a network error — the failure mode is one-way audio on the
   subset of calls whose ICE happened to need a relay.

   ```bash
   docker inspect mageride-coturn-1 --format '{{.HostConfig.NetworkMode}}'   # must be: host
   docker inspect mageride-livekit-1 --format '{{.HostConfig.NetworkMode}}'  # must be: host
   ```

2. **Is the advertised address right?** coturn cannot infer the address it tells peers to send to,
   and a wrong one is **silent** one-way audio rather than a failure. `DETECT_EXTERNAL_IP: "yes"` is
   the dev setting; a real deployment sets it explicitly.

3. **Is the token valid?** `LIVEKIT_KEYS` must equal `Voip__LiveKit__ApiKey` /
   `Voip__LiveKit__ApiSecret`. A mismatch is a refused join, not a slow one — that would be an error
   rate, not this alert.

4. **Is it one network?** Setup times concentrated on one mobile carrier is that carrier's NAT
   forcing relay. Nothing to fix on the platform; worth knowing.

---

## Fix

- Relay saturation → scale coturn, or place one closer to the users.
- Signalling slow → HAProxy proxies only the signalling WebSocket (ordinary HTTP upgrade traffic);
  check its backend health.
- Wrong network mode → put it back on host networking and restart.

---

## Instrumenting it

Record `mageride.voip.call_setup.latency` (unit `ms`) in voip-svc from the moment the room token is
issued to LiveKit's `participant_joined` / first-track webhook. The recording rule
`mageride:voip_setup:p95` already reads
`mageride_voip_call_setup_latency_milliseconds_bucket`.

---

## What not to do

- **Do not route media through HAProxy.** It cannot relay UDP; D6' §6 says so, and the compose file's
  comment explains the consequence in detail.
- **Do not publish the RTC port range through docker-proxy.** A hundred UDP ports through a userland
  hop, on a media path.
- **Do not treat this as a page.** VoIP is Phase 1 and masked calling has a fallback; the SLO is a
  ticket.
