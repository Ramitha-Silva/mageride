// =====================================================================================
// 62 — mass driver offline, through EMQX's Last Will and Testament (R-15, T-04).
//
//   k6 run chaos/k6/lwt.js -e CHAOS_FLEET=200
//
// R-15: "EMQX LWT not wired to dispatch → `veh/{vehicleId}/status=offline` event → dispatch-svc
// releases active offer / starts grace timer per ride state".
//
// A regional signal loss does not send DISCONNECT. It stops. So this drill connects a fleet with
// a will registered, then drops every socket WITHOUT a DISCONNECT — MQTT 3.1.1 §3.14 makes that
// the exact difference between "the app was closed" and "the phone lost coverage", and the will
// is published only in the second case. `MqttClient.abort()` exists for this (Δ C130); `close()`
// would send the DISCONNECT that suppresses it.
//
// ------------------------------------------------------------------------------------
// A WATCHER, BECAUSE "NOTHING HAPPENED" HAS TWO CAUSES
// ------------------------------------------------------------------------------------
// If the platform does not react to the wills, that is either because EMQX never published them
// or because nothing is listening. A second client subscribes to `veh/+/status` as a `svc-`
// principal — `acl.conf` line 28 grants `^svc-` the whole `veh/#` tree — and counts what the
// BROKER emitted. The drill script then reads the platform's side. The two together say which
// half is missing, which is the difference between a broker finding and a service finding.
// =====================================================================================

import { Counter, Trend } from 'k6/metrics';
import { MqttClient } from '../../load/lib/mqtt.js';
import { mqttSessionToken } from '../../load/lib/jwt.js';
import { config, requireConfigured, chaosVehicleId } from './lib/config.js';

const fleet = Number(__ENV.CHAOS_FLEET || 200);
const holdSeconds = Number(__ENV.CHAOS_HOLD || 8);
const watchSeconds = Number(__ENV.CHAOS_WATCH || 20);

// A driver whose vehicle is real and whose offer is live — passed in by the drill so the
// platform-side assertion is about a driver the dispatcher knows.
const realVehicle = __ENV.CHAOS_REAL_VEHICLE || '';

const willsConnected = new Counter('lwt_connected');
const willsAborted = new Counter('lwt_aborted');
const willsObserved = new Counter('lwt_wills_observed');
const realWillObserved = new Counter('lwt_real_will_observed');
const willLatency = new Trend('lwt_will_latency_ms', true);

export const options = {
  scenarios: {
    watcher: {
      executor: 'per-vu-iterations',
      exec: 'watcher',
      vus: 1,
      iterations: 1,
      maxDuration: `${holdSeconds + watchSeconds + 30}s`,
      gracefulStop: '5s',
    },
    fleet: {
      executor: 'per-vu-iterations',
      exec: 'device',
      vus: fleet,
      iterations: 1,
      startTime: '5s',
      maxDuration: `${holdSeconds + 30}s`,
      gracefulStop: '5s',
    },
  },
  insecureSkipTLSVerify: true,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'max'],
};

export function setup() {
  requireConfigured(['mqttUrl', 'mqttSecret']);
  console.log(`lwt: ${fleet} devices with a will, aborted after ${holdSeconds}s` +
    (realVehicle ? `; plus the fixture vehicle ${realVehicle}` : ''));
  return {};
}

// -------------------------------------------------------------------------------------
// The observer — what the BROKER did
// -------------------------------------------------------------------------------------
export function watcher() {
  const principal = 'svc-chaos-lwt';
  let abortedAt = 0;

  const client = new MqttClient({
    url: config.mqttUrl,
    clientId: `chaos-lwt-watch-${Date.now()}`,
    // The username IS the `vehicleId` claim: emqx.conf's `verify_claims` compares the two, and
    // acl.conf's `^svc-` rule is what then grants `veh/#`. Exactly how mqtt-bridge-svc and
    // tcp-adapter authenticate (load/lib/jwt.js's header records the whole argument).
    username: principal,
    password: mqttSessionToken(principal, config.mqttSecret),
    keepAlive: 0,
    onOpen: (self) => {
      self.subscribe('veh/+/status');
      // The fleet starts at +5 s and aborts at +5+hold.
      abortedAt = Date.now() + (5 + holdSeconds) * 1000;
      setTimeout(() => self.close(), (holdSeconds + watchSeconds + 10) * 1000);
    },
    onMessage: (message) => {
      let text = '';
      for (let i = 0; i < message.payload.length; i++) {
        text += String.fromCharCode(message.payload[i]);
      }

      if (text.trim() !== 'offline') {
        return;
      }

      willsObserved.add(1);
      if (abortedAt > 0) {
        willLatency.add(Math.max(0, Date.now() - abortedAt));
      }
      if (realVehicle && message.topic.indexOf(realVehicle) >= 0) {
        realWillObserved.add(1);
      }
    },
    onError: (message) => console.error(`lwt watcher: ${message}`),
  });

  return client;
}

// -------------------------------------------------------------------------------------
// One device that is about to lose coverage
// -------------------------------------------------------------------------------------
export function device() {
  // VU 1 stands in for the fixture driver when the drill supplied one, so the platform-side
  // assertion is about a vehicle dispatch-svc has an offer for.
  const id = (realVehicle && __VU === 1) ? realVehicle : chaosVehicleId(__VU);

  const client = new MqttClient({
    url: config.mqttUrl,
    clientId: `chaos-lwt-${id}`,
    username: id,
    password: mqttSessionToken(id, config.mqttSecret),
    keepAlive: 0,
    // The whole point. `retain: true` because MqttTopics.cs calls this "the retained LWT payload
    // (R-15, T-04)" and emqx.conf sets `retain_available = true` with that topic named in its
    // comment; QoS 1 because D6' §3.1 makes every topic in the tree QoS 1.
    will: { topic: `veh/${id}/status`, payload: 'offline', qos: 1, retain: true },
    onOpen: (self) => {
      willsConnected.add(1);
      // `online` first, as a real session does — otherwise the platform sees an `offline` for a
      // vehicle it never saw arrive, and dispatch-svc's `CameOnlineAsync`/`WentOfflineAsync` pair
      // is never exercised in order.
      self.publish(`veh/${id}/status`, Array.from(new Uint8Array([0x6f, 0x6e, 0x6c, 0x69, 0x6e, 0x65])));

      setTimeout(() => {
        self.abort();
        willsAborted.add(1);
      }, holdSeconds * 1000);
    },
    onError: (message) => {
      if (__VU <= 3) {
        console.log(`lwt device ${__VU}: ${message}`);
      }
    },
  });

  return client;
}
