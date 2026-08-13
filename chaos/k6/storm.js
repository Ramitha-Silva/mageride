// =====================================================================================
// 60 — the reconnect storm (R-09).
//
//   k6 run chaos/k6/storm.js -e CHAOS_SESSIONS=600 -e CHAOS_ROUNDS=3
//
// ADD §7.5.3 names three controls and this drives the first:
//
//   "EMQX connection rate limit per listener (e.g. 500 new connections/s/listener) + per ASN
//    guardrail, so a regional 4G outage recovery cannot flood the broker."
//
// `infra/deploy/emqx/emqx.conf` sets `max_conn_rate = "500/s"` on all three listeners. The
// question a drill can answer is what happens to the connections ABOVE that line — refused, or
// queued and served late — and whether the broker keeps serving the ones already on it. A
// regional outage recovery is not a load test; it is every device on the network arriving in the
// same second, and the platform's promise is that the ones already connected do not notice.
//
// ------------------------------------------------------------------------------------
// WHAT IS DELIBERATELY NOT DONE HERE
// ------------------------------------------------------------------------------------
// The per-ASN guardrail is not exercised. Every connection on this box has the same source
// address (HAProxy's, from the containers' point of view), which is the same deployment property
// that makes the gateway's per-caller rate limit a per-platform one — load/report.md's finding.
// An ASN limit cannot be observed where there is one ASN.
// =====================================================================================

import { Counter, Rate, Trend } from 'k6/metrics';
import { MqttClient } from '../../load/lib/mqtt.js';
import { mqttSessionToken } from '../../load/lib/jwt.js';
import { encodePosition } from '../../load/lib/cbor.js';
import { config, requireConfigured, chaosVehicleId } from './lib/config.js';

const sessions = Number(__ENV.CHAOS_SESSIONS || 600);
const rounds = Number(__ENV.CHAOS_ROUNDS || 3);

// The session that was already connected when the storm began. Its samples are the measurement
// that matters: R-09 exists so that a reconnect flood does not cost the devices that never left.
const incumbentIndex = 999999;

const connackMs = new Trend('storm_connack_ms', true);
const connected = new Counter('storm_connected');
const refused = new Counter('storm_refused');
const connectOk = new Rate('storm_connect_ok');
const incumbentAcked = new Counter('storm_incumbent_acked');
const incumbentPublished = new Counter('storm_incumbent_published');

export const options = {
  scenarios: {
    // The incumbent connects first and publishes throughout.
    incumbent: {
      executor: 'per-vu-iterations',
      exec: 'incumbent',
      vus: 1,
      iterations: 1,
      maxDuration: `${8 + rounds * 6 + 15}s`,
      gracefulStop: '5s',
    },
    // …then the storm arrives, all at once, `rounds` times.
    storm: {
      executor: 'per-vu-iterations',
      exec: 'stormer',
      vus: sessions,
      iterations: rounds,
      startTime: '8s',
      maxDuration: `${rounds * 6 + 10}s`,
      gracefulStop: '5s',
    },
  },
  insecureSkipTLSVerify: true,
  // No thresholds: this profile's job is to report what the broker did, and the drill script
  // decides what that means against ADD §14.1. A threshold here would make `k6 run` the judge of
  // a spec question.
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  requireConfigured(['mqttUrl', 'mqttSecret']);
  console.log(`storm: ${sessions} sessions x ${rounds} rounds against ${config.mqttUrl}`);
  return {};
}

// -------------------------------------------------------------------------------------
// The device that was already there
// -------------------------------------------------------------------------------------
export function incumbent() {
  const id = chaosVehicleId(incumbentIndex);
  let seq = Date.now();
  let done = false;

  const client = new MqttClient({
    url: config.mqttUrl,
    clientId: `chaos-incumbent-${id}`,
    username: id,
    password: mqttSessionToken(id, config.mqttSecret),
    keepAlive: 0,
    onOpen: (self) => {
      const timer = setInterval(() => {
        seq = Date.now();
        // Stationary, at Colombo Fort. A drill that moved would be measuring the plausibility
        // gate as well as the broker.
        self.publish(`veh/${id}/pos/live`, encodePosition({
          vehicleId: id,
          sampleTs: seq,
          seq,
          lat: 6.9271,
          lng: 79.8612,
          source: 0,
          speedMps: 0,
          headingDeg: 0,
          accuracyM: 8,
          satCount: 11,
          mode: 'C',
          vehicleType: 'three_wheeler',
        }));
        incumbentPublished.add(1);
      }, 1000);

      setTimeout(() => {
        clearInterval(timer);
        done = true;
        self.close();
      }, (rounds * 6 + 12) * 1000);
    },
    onAck: () => incumbentAcked.add(1),
    onError: (message) => {
      if (!done) {
        console.error(`incumbent: ${message}`);
      }
    },
  });

  // k6 keeps the VU alive while the socket is open; nothing to await.
  return client;
}

// -------------------------------------------------------------------------------------
// One arriving device, three times
// -------------------------------------------------------------------------------------
export function stormer() {
  const id = chaosVehicleId(__VU);
  const started = Date.now();

  new MqttClient({
    url: config.mqttUrl,
    // The iteration is in the client id: MQTT disconnects the previous session holding the same
    // id, so a VU reconnecting with one id would be measuring the broker's takeover path rather
    // than a new connection.
    clientId: `chaos-storm-${__VU}-${__ITER}`,
    username: id,
    password: mqttSessionToken(id, config.mqttSecret),
    keepAlive: 0,
    onOpen: (self) => {
      connackMs.add(Date.now() - started);
      connected.add(1);
      connectOk.add(true);
      // Straight back off, which is what a reconnect storm is: connect, be told the session is
      // live, and leave. `close()` and not `abort()` — these devices have no will and a will
      // storm is drill 62.
      self.close();
    },
    onError: (message) => {
      refused.add(1);
      connectOk.add(false);
      if (__VU <= 3) {
        console.log(`storm refusal (VU ${__VU}, iter ${__ITER}): ${message}`);
      }
    },
  });
}
