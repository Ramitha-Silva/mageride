// =====================================================================================
// C129 — the ingest profile: EMQX -> mqtt-bridge -> telemetry.raw -> position-processor ->
// Redis -> fanout-svc -> a subscribed passenger.
//
//   k6 run load/ingest.js --summary-export=load/out/ingest.json      # the manifest's verify
//   k6 run load/ingest.js -e PROFILE=burst
//   k6 run load/ingest.js -e PROFILE=fleet
//   bash load/run.sh                                                 # every profile + the report
//
// Prerequisite: `bash load/configure.sh` (the replica up, load/env.json written).
//
// ------------------------------------------------------------------------------------
// WHAT IS MEASURED, AND WHAT THAT COSTS
// ------------------------------------------------------------------------------------
// ADD §3.2 gives ingest as a MESSAGE RATE (3,000 msg/s sustained, 15,000 burst) and §16.1
// derives that rate from a VEHICLE COUNT (10,000 vehicles x 0.12 msg/s blended). The two are
// not the same load, and the replica can only host one of them:
//
//   * reaching 3,000 msg/s the way production does needs 25,000 concurrent MQTT sessions,
//     which is 12x the sessions the replica's 2 GB EMQX is sized for;
//   * reaching it with 750 sessions at 4 msg/s each is the same work for the bridge, the
//     processor, Redpanda and Redis, and a twelfth of the work for the broker's session table.
//
// So `sustained` and `burst` publish the production MESSAGE rate from a smaller fleet, and
// `fleet` publishes the production per-vehicle CADENCE from as many sessions as the box will
// hold. Every number in load/report.md says which of the two it came from. Nothing here reports
// a connection-count target as met.
//
// The one thing NEITHER profile drives is the hardware-tracker plane: EMQX's 8883 listener is
// mutual-TLS (`peer_cert_as_username = cn`) and the GT06/JT808/H02 families arrive as TCP
// frames at tcp-adapter, neither of which a WebSocket client can speak. T-10's +100k trackers
// at 0.2 Hz is therefore extrapolated, not measured. load/README.md says what that leaves open.
// =====================================================================================

import { Counter, Trend, Rate } from 'k6/metrics';
import { MqttClient } from './lib/mqtt.js';
import { encodePosition } from './lib/cbor.js';
import { mqttSessionToken } from './lib/jwt.js';
import { config, requireConfigured } from './lib/config.js';
import { Vehicle, positionTopic } from './lib/fleet.js';
import { LiveHubClient, frameKey } from './lib/signalr.js';

// -------------------------------------------------------------------------------------
// Profiles
// -------------------------------------------------------------------------------------

const PROFILES = {
  // A minute end to end, enough to prove the chain and the fixture. Not a capacity claim.
  smoke: { connections: 25, ratePerConnection: 4, durationSeconds: 30 },

  // ADD §3.2's launch target: 750 x 4 = 3,000 msg/s.
  sustained: { connections: 750, ratePerConnection: 4, durationSeconds: 180 },

  // ADD §3.2's launch burst: 3,750 x 4 = 15,000 msg/s. Short, because a burst is.
  burst: { connections: 3750, ratePerConnection: 4, durationSeconds: 60 },

  // §16.1's shape rather than its rate: sessions at the blended 0.12 msg/s/vehicle, to price
  // what a CONNECTION costs the broker as opposed to what a MESSAGE costs the pipeline.
  fleet: { connections: 3000, ratePerConnection: 0.12, durationSeconds: 120 },
};

const profileName = __ENV.PROFILE || 'sustained';
const profile = PROFILES[profileName];

if (!profile) {
  throw new Error(
    `Unknown PROFILE '${profileName}'. One of: ${Object.keys(PROFILES).join(', ')}.`);
}

const connections = Number(__ENV.LOAD_CONNECTIONS || profile.connections);
const ratePerConnection = Number(__ENV.LOAD_RATE || profile.ratePerConnection);
const durationSeconds = Number(__ENV.LOAD_DURATION || profile.durationSeconds);

const targetRate = connections * ratePerConnection;

// EMQX refuses connections above `max_conn_rate = "500/s"` per listener (R-09's reconnect-storm
// control). 250/s leaves room for the retries a refused CONNECT would cause and keeps the ramp
// from being measured as the load.
const rampSeconds = Math.max(5, Math.ceil(connections / 250));

// One VU per ~25 sockets. A k6 VU is one JavaScript event loop, so the sockets it holds share a
// thread: too few VUs and the publish interval slips under its own callbacks, too many and the
// runtime costs more than the system under test. 25 x 4 Hz = 100 publishes/s/VU.
const vus = Math.min(Number(__ENV.LOAD_VUS || 240), Math.max(1, Math.ceil(connections / 25)));
const perVu = Math.ceil(connections / vus);

// Two subscribers per cell is enough to make the geocell pump run — without a member in a cell
// fanout-svc reads nothing and the D-19 SLO is unmeasurable — while leaving the subscriber-scale
// question to load/fanout.js, where it is the subject rather than a confounder.
const watchersEnabled = __ENV.LOAD_WATCH !== '0' && !!config.watcherToken;

const graceSeconds = 30;

// -------------------------------------------------------------------------------------
// Metrics
// -------------------------------------------------------------------------------------

const connectMs = new Trend('mqtt_connect_ms', true);
const connectOk = new Rate('mqtt_connect_ok');
const published = new Counter('mqtt_published');
const acknowledged = new Counter('mqtt_puback');
const backpressured = new Counter('mqtt_backpressure_skips');
const brokerErrors = new Counter('mqtt_errors');

const e2eMs = new Trend('position_e2e_ms', true);
const hubMessages = new Counter('fanout_messages');
const hubFrames = new Counter('fanout_frames');
const hubMatched = new Counter('fanout_matched');
const hubUnmatched = new Counter('fanout_unmatched');
const hubOk = new Rate('fanout_connect_ok');

// -------------------------------------------------------------------------------------
// Options
// -------------------------------------------------------------------------------------

// 95% of the target, over the measurement window alone. A run that published a tenth of the
// rate and enjoyed excellent latency would otherwise report the SLO as met at 3,000 msg/s.
const requiredAcks = Math.floor(targetRate * durationSeconds * 0.95);

const thresholds = {
  // D-19 / ADD §3.2. THIS is the definition of done, and it is deliberately allowed to fail:
  // a profile the replica cannot carry must not exit 0.
  position_e2e_ms: ['p(95)<5000', 'p(99)<8000'],
  mqtt_puback: [`count>${requiredAcks}`],
  mqtt_connect_ok: ['rate>0.99'],
};

if (!watchersEnabled) {
  // Nothing subscribed, so no frame is pushed and the SLO threshold would pass on an empty
  // population. Removed rather than softened, and run.sh refuses to accept such a run as
  // evidence for the DoD.
  delete thresholds.position_e2e_ms;
}

export const options = {
  scenarios: {
    ingest: {
      executor: 'per-vu-iterations',
      vus,
      iterations: 1,
      maxDuration: `${rampSeconds + durationSeconds + graceSeconds + 60}s`,
      gracefulStop: '30s',
    },
  },
  // The replica's edge certificate is self-signed by design (`infra/replica/deploy.sh`), which
  // is the same reason every other script on this box passes `curl -k`.
  insecureSkipTLSVerify: true,
  thresholds,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

// -------------------------------------------------------------------------------------
// Setup — one clock for every VU
// -------------------------------------------------------------------------------------

export function setup() {
  requireConfigured(['mqttUrl', 'mqttSecret']);

  if (!watchersEnabled) {
    console.warn(
      'No watcherToken in load/env.json (or LOAD_WATCH=0): the SignalR half is off, so this run ' +
      'measures ingest throughput ONLY and proves nothing about the D-19 end-to-end SLO.');
  }

  const startedAt = Date.now();

  console.log(
    `profile=${profileName} connections=${connections} rate=${ratePerConnection}/s/conn ` +
    `target=${targetRate} msg/s vus=${vus} ramp=${rampSeconds}s window=${durationSeconds}s`);

  return {
    startedAt,
    // Publishing begins as sockets connect; only what happens after the ramp is measured, so a
    // half-connected fleet never contributes to a rate claim.
    measureFrom: startedAt + rampSeconds * 1000,
    stopAt: startedAt + (rampSeconds + durationSeconds) * 1000,
    cells: config.cellsByVehicle || {},
  };
}

// -------------------------------------------------------------------------------------
// The VU
// -------------------------------------------------------------------------------------

export default function (plan) {
  const first = (__VU - 1) * perVu;
  const last = Math.min(connections, first + perVu);

  if (first >= connections) {
    return;
  }

  // Publish instant per (vehicle, position). The D-19 measurement is the only reason this
  // exists, and it is VU-local because k6 VUs share no memory: the connection that watches a
  // cell has to be held by the same VU as the vehicles publishing into it, which is why the
  // fleet is laid out in clusters and each VU takes whole clusters.
  const publishedAt = new Map();
  const clients = [];
  let watcher = null;

  // 30 seconds of this VU's own publishing — comfortably past the 8 s the D-19 p99 allows and
  // past the 60 s freshness window's effect on nothing here, while staying a few thousand
  // entries rather than an unbounded map.
  const historyEntries = Math.max(500, Math.ceil((last - first) * ratePerConnection * 30));

  // ---------------------------------------------------------------------------------
  // The subscriber half
  // ---------------------------------------------------------------------------------
  if (watchersEnabled) {
    const wanted = [];
    for (let index = first; index < last; index++) {
      const cell = plan.cells[String(index)];
      if (cell && wanted.indexOf(cell) === -1) {
        wanted.push(cell);
      }
    }

    if (wanted.length > 0) {
      watcher = new LiveHubClient({
        edge: config.edge,
        token: config.watcherToken,
        onReady: (hub) => {
          hubOk.add(true);
          // `Fanout:MaxCellsPerConnection` is 128 and a VU holds a handful; the slice is a fence
          // against a mis-sized profile rather than an expected path.
          hub.joinGeocells(wanted.slice(0, 128));
        },
        onEvent: (target, args) => {
          if (target !== 'VehiclePositions') {
            return;
          }

          const now = Date.now();
          const frames = args[0] || [];

          hubMessages.add(1);
          hubFrames.add(frames.length);

          for (const frame of frames) {
            const sentAt = publishedAt.get(frameKey(frame.vehicleId, frame.lat, frame.lng));

            if (sentAt === undefined) {
              // A frame for a position this VU did not publish — a vehicle whose orbit crossed a
              // cell boundary, or a leftover from a previous run still inside the freshness
              // window. Counted rather than ignored, because a high share would mean the
              // latency population is not the population being published.
              hubUnmatched.add(1);
              continue;
            }

            hubMatched.add(1);

            if (sentAt >= plan.measureFrom && sentAt <= plan.stopAt) {
              e2eMs.add(now - sentAt);
            }
          }
        },
        onError: (message) => {
          hubOk.add(false);
          console.error(`hub: ${message}`);
        },
      });
    }
  }

  // ---------------------------------------------------------------------------------
  // The publisher half
  // ---------------------------------------------------------------------------------
  const intervalMs = 1000 / ratePerConnection;

  for (let index = first; index < last; index++) {
    const vehicle = new Vehicle(index, {
      originLat: config.originLat,
      originLng: config.originLng,
    });

    // Spread globally, not per VU: EMQX's per-listener connection ceiling counts the whole box,
    // so every VU staggering independently would still arrive in one wave.
    const openAt = (index / Math.max(1, connections)) * rampSeconds * 1000;

    setTimeout(() => {
      const startedConnect = Date.now();

      const client = new MqttClient({
        url: config.mqttUrl,
        // The vehicle id, not a random one: two sessions presenting one client id make EMQX
        // disconnect the first, which across a fleet reads as a flapping broker.
        clientId: `load-${vehicle.id}`,
        username: vehicle.id,
        password: mqttSessionToken(vehicle.id, config.mqttSecret),
        // 0: the run is shorter than any sensible keep-alive and a PINGREQ per socket per
        // 30 s is 125 packets/s of measurement noise at the burst profile's socket count.
        keepAlive: 0,
        onOpen: (self) => {
          connectMs.add(Date.now() - startedConnect);
          connectOk.add(true);

          const topic = positionTopic(vehicle.id);
          const startedPublishing = Date.now();
          let due = 0;

          // Catch-up scheduling, not a plain `setInterval(publish, 1000/rate)`.
          //
          // k6's timers are not drift-corrected: each interval is the period PLUS whatever the
          // callback cost, and with 25 sockets per VU that compounds into a real shortfall — the
          // first version of this file asked for 100 msg/s and delivered 89, which would have
          // been reported as the platform failing to keep up. Deriving the number of samples owed
          // from elapsed time makes the generator hold its rate, and anything the platform cannot
          // absorb then shows up where it belongs: as backpressure skips.
          const timer = setInterval(() => {
            const now = Date.now();

            if (now > plan.stopAt) {
              clearInterval(timer);
              self.close();
              return;
            }

            const owed = Math.floor((now - startedPublishing) / intervalMs);

            // At most four in one tick. A generator that tried to repay a long stall in one go
            // would hand EMQX a burst the D-17 limiter would pace anyway, and the run would
            // measure the recovery rather than the rate.
            let budget = 4;

            while (due < owed && budget-- > 0) {
              // The broker paces a publisher over `messages_rate = "5/s"` (D-17) by not reading
              // from the socket, so an unbounded publisher would queue in k6 rather than in EMQX
              // and the measured rate would be the generator's, not the platform's.
              if (self.inFlight > 64) {
                backpressured.add(1);
                break;
              }

              const sample = vehicle.next();

              if (!self.publish(topic, encodePosition(sample))) {
                break;
              }

              due++;

              if (now >= plan.measureFrom) {
                published.add(1);
              }

              if (watcher !== null) {
                publishedAt.set(frameKey(sample.vehicleId, sample.lat, sample.lng), now);

                // Sized by TIME, not by a round number. The window has to outlast the SLO being
                // measured — 2 s of fan-out batching plus up to 8 s at the D-19 p99 — or the
                // entry is evicted before its frame arrives and the observation is lost as
                // "unmatched". A fixed 400 entries was 4 s at this profile's rate, which threw
                // away precisely the slow tail the p99 is about.
                if (publishedAt.size > historyEntries) {
                  const oldest = publishedAt.keys().next().value;
                  publishedAt.delete(oldest);
                }
              }
            }
          }, Math.min(intervalMs, 250));
        },
        onAck: () => {
          if (Date.now() >= plan.measureFrom) {
            acknowledged.add(1);
          }
        },
        onError: (message) => {
          brokerErrors.add(1);
          connectOk.add(false);
          if (brokerErrors.name && __VU === 1) {
            console.error(`mqtt: ${message}`);
          }
        },
      });

      clients.push(client);
    }, openAt);
  }

  // The VU's iteration ends when its event loop drains. Everything above is a timer, so this is
  // what holds the iteration open for the run and then takes it down in one place.
  setTimeout(() => {
    for (const client of clients) {
      client.close();
    }
    if (watcher !== null) {
      watcher.close();
    }
  }, (rampSeconds + durationSeconds + graceSeconds) * 1000);
}

// -------------------------------------------------------------------------------------
// Summary
// -------------------------------------------------------------------------------------

export function handleSummary(data) {
  const count = (name) => {
    const metric = data.metrics[name];
    return metric && metric.values ? metric.values.count || 0 : 0;
  };
  const trend = (name, stat) => {
    const metric = data.metrics[name];
    return metric && metric.values ? metric.values[stat] : undefined;
  };

  const achieved = count('mqtt_puback') / durationSeconds;

  const result = {
    component: 'C129',
    profile: profileName,
    plan: {
      connections,
      ratePerConnection,
      targetMsgPerSecond: targetRate,
      windowSeconds: durationSeconds,
      rampSeconds,
      vus,
      watchersEnabled,
    },
    measured: {
      publishedInWindow: count('mqtt_published'),
      acknowledgedInWindow: count('mqtt_puback'),
      achievedMsgPerSecond: Number(achieved.toFixed(1)),
      achievedFractionOfTarget: Number((achieved / targetRate).toFixed(3)),
      backpressureSkips: count('mqtt_backpressure_skips'),
      brokerErrors: count('mqtt_errors'),
      connectMs: {
        med: trend('mqtt_connect_ms', 'med'),
        p95: trend('mqtt_connect_ms', 'p(95)'),
        max: trend('mqtt_connect_ms', 'max'),
      },
    },
    endToEnd: watchersEnabled
      ? {
          // The Trend's own `count` is not exposed in this k6's summary shape, so the population
          // is the matched-frame counter — the same events, counted where they are correlated.
          samples: count('fanout_matched'),
          medMs: trend('position_e2e_ms', 'med'),
          p95Ms: trend('position_e2e_ms', 'p(95)'),
          p99Ms: trend('position_e2e_ms', 'p(99)'),
          maxMs: trend('position_e2e_ms', 'max'),
          sloMet:
            trend('position_e2e_ms', 'p(95)') < 5000 && trend('position_e2e_ms', 'p(99)') < 8000,
          hubMessages: count('fanout_messages'),
          hubFrames: count('fanout_frames'),
          matched: count('fanout_matched'),
          unmatched: count('fanout_unmatched'),
        }
      : null,
  };

  const lines = [
    '',
    `  C129 ingest — profile ${profileName}`,
    `  target        ${targetRate} msg/s from ${connections} sessions at ${ratePerConnection}/s`,
    `  achieved      ${result.measured.achievedMsgPerSecond} msg/s ` +
      `(${(result.measured.achievedFractionOfTarget * 100).toFixed(1)}% of target)`,
    `  connect       med ${fmt(result.measured.connectMs.med)} ms  p95 ${fmt(result.measured.connectMs.p95)} ms`,
    `  backpressure  ${result.measured.backpressureSkips} skipped publishes, ${result.measured.brokerErrors} broker errors`,
  ];

  if (result.endToEnd) {
    lines.push(
      `  D-19 e2e      p95 ${fmt(result.endToEnd.p95Ms)} ms (< 5000)  ` +
        `p99 ${fmt(result.endToEnd.p99Ms)} ms (< 8000)  over ${result.endToEnd.samples} observations`,
      `  fan-out       ${result.endToEnd.hubMessages} hub messages carrying ` +
        `${result.endToEnd.hubFrames} frames; ${result.endToEnd.unmatched} unmatched`);
  } else {
    lines.push('  D-19 e2e      NOT MEASURED — no subscriber half in this run');
  }

  lines.push('');

  return {
    stdout: lines.join('\n'),
    [`load/out/ingest-${profileName}.json`]: JSON.stringify(result, null, 2),
  };
}

function fmt(value) {
  return value === undefined ? 'n/a' : value.toFixed(0);
}
