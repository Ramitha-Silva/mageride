// =====================================================================================
// 61 — the replay flood (R-09, R-17, T-05).
//
//   k6 run chaos/k6/replay-flood.js -e CHAOS_FLEET=40 -e CHAOS_DURATION=30
//
// R-09's second half: "separate `veh/{vehicleId}/pos/live` vs `pos/replay` topics; replay
// throttled". ADD §7.5.1 states the purpose — the split "prevents a reconnect storm, where every
// vehicle replays its local buffer, from drowning live samples; mqtt-bridge-svc consumes both but
// applies a lower rate-limit and lower priority to `pos/replay`".
//
// So the test is not "can the platform absorb a flood" — it is "does a flood on the replay lane
// cost the LIVE lane anything". One control vehicle publishes live at the ordinary cadence for
// the whole run while the fleet dumps its backlog, and the two lanes are counted separately.
//
// The deployed knobs this is aimed at (`.env.app.example`, read back off the container):
//   MqttBridge__ThrottleReplay=true · ReplaySamplesPerSecond=20 · ReplayQueueDepth=256
//   MqttBridge__ReplayMaxWait=00:00:30 · ReplayLaneIdleTimeout=00:05:00
//
// Each vehicle publishes at 5/s, which is D-17's per-vehicle ceiling exactly
// (`messages_rate = "5/s"` on the listener and `MqttBridge__PublishCeilingPerSecond=5`); the
// flood is made of vehicle COUNT, not of one vehicle misbehaving, because a per-vehicle limiter
// would refuse the latter at the broker and the drill would never reach the bridge.
// =====================================================================================

import { Counter, Trend } from 'k6/metrics';
import { MqttClient } from '../../load/lib/mqtt.js';
import { mqttSessionToken } from '../../load/lib/jwt.js';
import { encodePosition } from '../../load/lib/cbor.js';
import { config, requireConfigured, chaosVehicleId } from './lib/config.js';

const fleet = Number(__ENV.CHAOS_FLEET || 40);
const durationSeconds = Number(__ENV.CHAOS_DURATION || 30);
const perVehicleRate = Number(__ENV.CHAOS_RATE || 5);

// How long the control session stays open after its last publish, so every in-flight PUBACK can
// land before the socket goes. Six seconds against a measured worst-case acknowledgement of ~1.3 s
// under flood — generous on purpose, because the cost of being wrong is a false HIGH finding.
const drainSeconds = Number(__ENV.CHAOS_DRAIN || 6);

const controlIndex = 999998;

const replayPublished = new Counter('flood_replay_published');
const replayAcked = new Counter('flood_replay_acked');
const livePublished = new Counter('flood_live_published');
const liveAcked = new Counter('flood_live_acked');
const liveAckMs = new Trend('flood_live_ack_ms', true);

export const options = {
  scenarios: {
    control: {
      executor: 'per-vu-iterations',
      exec: 'control',
      vus: 1,
      iterations: 1,
      maxDuration: `${durationSeconds + 40}s`,
      gracefulStop: '15s',
    },
    flood: {
      executor: 'per-vu-iterations',
      exec: 'replayer',
      vus: fleet,
      iterations: 1,
      startTime: '6s',
      maxDuration: `${durationSeconds + 30}s`,
      gracefulStop: '10s',
    },
  },
  insecureSkipTLSVerify: true,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  requireConfigured(['mqttUrl', 'mqttSecret']);
  console.log(`replay flood: ${fleet} vehicles x ${perVehicleRate}/s on pos/replay for ` +
    `${durationSeconds}s, one control vehicle on pos/live`);
  return {};
}

function sample(id, seq, lat, lng) {
  return encodePosition({
    vehicleId: id,
    sampleTs: seq,
    seq,
    lat,
    lng,
    source: 0,
    speedMps: 0,
    headingDeg: 0,
    accuracyM: 8,
    satCount: 11,
    mode: 'C',
    vehicleType: 'three_wheeler',
  });
}

// -------------------------------------------------------------------------------------
// The vehicle that never went offline
// -------------------------------------------------------------------------------------
export function control() {
  const id = chaosVehicleId(controlIndex);
  let seq = Date.now();

  // A FIFO of send instants. QoS-1 PUBACKs may be coalesced into one frame but never reorder
  // within a session, and this session has exactly one publisher, so the oldest outstanding send
  // is always the one being acknowledged. That makes `shift()` an exact match rather than an
  // approximation, and it is the whole reason the number below can be called an ack latency.
  const sentAt = [];

  const client = new MqttClient({
    url: config.mqttUrl,
    clientId: `chaos-flood-control-${id}`,
    username: id,
    password: mqttSessionToken(id, config.mqttSecret),
    keepAlive: 0,
    onOpen: (self) => {
      const timer = setInterval(() => {
        seq = Date.now();
        sentAt.push(Date.now());
        self.publish(`veh/${id}/pos/live`, sample(id, seq, 6.9271, 79.8612));
        livePublished.add(1);
      }, 1000);

      // Stop publishing, THEN drain, THEN close. The two are separated by `drainSeconds` because
      // a QoS-1 PUBACK is not instantaneous under a flood — the drill's own measurement puts the
      // maximum over a second — and closing the socket on the heels of the last publish loses its
      // acknowledgement. That is a tail-shaped loss the drill cannot tell from the broker dropping
      // live samples, and on one run it produced a HIGH finding ("a replay flood drowns live
      // samples") from five unacked publishes at shutdown. The generator must not be able to
      // manufacture the failure it is looking for.
      setTimeout(() => clearInterval(timer), (durationSeconds + 8) * 1000);
      setTimeout(() => self.close(), (durationSeconds + 8 + drainSeconds) * 1000);
    },
    onAck: () => {
      liveAcked.add(1);
      const at = sentAt.shift();
      if (at !== undefined) {
        liveAckMs.add(Date.now() - at);
      }
    },
    onError: (message) => console.error(`flood control: ${message}`),
  });

  return client;
}

// -------------------------------------------------------------------------------------
// One vehicle emptying its offline buffer
// -------------------------------------------------------------------------------------
export function replayer() {
  const id = chaosVehicleId(__VU);
  // A backlog is OLD. Timestamps run from an hour ago forward, which is what makes these replay
  // samples rather than live ones arriving on the wrong topic — and `seq` is the R-17/T-05
  // watermark, so it has to increase strictly per vehicle or the processor discards the rest of
  // the buffer as already-seen.
  let seq = Date.now() - 3600_000;
  let owed = 0;
  const startedAt = Date.now();

  const client = new MqttClient({
    url: config.mqttUrl,
    clientId: `chaos-flood-${id}`,
    username: id,
    password: mqttSessionToken(id, config.mqttSecret),
    keepAlive: 0,
    onOpen: (self) => {
      // Catch-up scheduled, not `setInterval(publish, 1000/rate)`: k6's timers are not
      // drift-corrected and the callback cost compounds, so a fixed interval under-delivers and
      // the shortfall reads as the platform refusing samples (load/CLAUDE.md records the run
      // where 100 msg/s was asked for and 89 delivered).
      const timer = setInterval(() => {
        const elapsed = (Date.now() - startedAt) / 1000;
        const target = Math.floor(elapsed * perVehicleRate);

        while (owed < target) {
          seq += 200;
          self.publish(`veh/${id}/pos/replay`, sample(id, seq, 6.9271 + __VU * 0.0005, 79.8612));
          replayPublished.add(1);
          owed++;
        }
      }, 100);

      setTimeout(() => {
        clearInterval(timer);
        self.close();
      }, durationSeconds * 1000);
    },
    onAck: () => replayAcked.add(1),
    onError: (message) => {
      if (__VU <= 3) {
        console.log(`flood replayer ${__VU}: ${message}`);
      }
    },
  });

  return client;
}
