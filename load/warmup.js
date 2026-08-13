// =====================================================================================
// C129 — the fixture run. Publishes one orbit per vehicle and stops.
//
//   k6 run load/warmup.js -e LOAD_CONNECTIONS=750
//
// Called by `load/configure.sh`, which samples `veh:meta:{vehicleId}` while this runs and
// writes the vehicle -> res-7 cell map into load/env.json. That map is what lets a subscriber
// join the cells its own vehicles publish into without an H3 implementation on this side.
//
// It is also the cheapest possible check that the whole chain is alive: a vehicle with no
// `veh:meta` hash after this has been refused somewhere between EMQX and Redis, and
// configure.sh says which count is short rather than leaving a profile to fail at scale.
// =====================================================================================

import { Counter } from 'k6/metrics';
import { MqttClient } from './lib/mqtt.js';
import { encodePosition } from './lib/cbor.js';
import { mqttSessionToken } from './lib/jwt.js';
import { config, requireConfigured } from './lib/config.js';
import { Vehicle, positionTopic } from './lib/fleet.js';

const connections = Number(__ENV.LOAD_CONNECTIONS || 750);

// A whole orbit at 4 Hz: the fleet turns 3 degrees per step, so 120 steps closes the loop and
// every vehicle ends where it started. That matters — configure.sh reads the cell at several
// points and the run itself starts from step 0, so a closed orbit is what keeps the recorded
// cell true for the whole of a later profile.
const samples = Number(__ENV.LOAD_WARMUP_SAMPLES || 120);
const ratePerConnection = 4;

const rampSeconds = Math.max(3, Math.ceil(connections / 250));
const vus = Math.max(1, Math.min(240, Math.ceil(connections / 25)));
const perVu = Math.ceil(connections / vus);
const publishSeconds = Math.ceil(samples / ratePerConnection);

const sent = new Counter('warmup_published');
const acked = new Counter('warmup_puback');
const failed = new Counter('warmup_errors');

export const options = {
  scenarios: {
    warmup: {
      executor: 'per-vu-iterations',
      vus,
      iterations: 1,
      maxDuration: `${rampSeconds + publishSeconds + 60}s`,
      gracefulStop: '20s',
    },
  },
  insecureSkipTLSVerify: true,
  summaryTrendStats: ['avg', 'med', 'p(95)', 'max'],
};

export function setup() {
  requireConfigured(['mqttUrl', 'mqttSecret']);
  console.log(`warm-up: ${connections} vehicles x ${samples} samples`);
  return { startedAt: Date.now() };
}

export default function (plan) {
  const first = (__VU - 1) * perVu;
  const last = Math.min(connections, first + perVu);
  const clients = [];

  for (let index = first; index < last; index++) {
    const vehicle = new Vehicle(index, {
      originLat: config.originLat,
      originLng: config.originLng,
    });

    setTimeout(() => {
      const client = new MqttClient({
        url: config.mqttUrl,
        clientId: `warm-${vehicle.id}`,
        username: vehicle.id,
        password: mqttSessionToken(vehicle.id, config.mqttSecret),
        keepAlive: 0,
        onOpen: (self) => {
          const topic = positionTopic(vehicle.id);
          const timer = setInterval(() => {
            if (vehicle.sent >= samples) {
              clearInterval(timer);
              self.close();
              return;
            }
            if (self.inFlight > 32) {
              return;
            }
            if (self.publish(topic, encodePosition(vehicle.next()))) {
              sent.add(1);
            }
          }, 1000 / ratePerConnection);
        },
        onAck: () => acked.add(1),
        onError: (message) => {
          failed.add(1);
          if (__VU === 1) {
            console.error(`warm-up: ${message}`);
          }
        },
      });

      clients.push(client);
    }, (index / Math.max(1, connections)) * rampSeconds * 1000);
  }

  setTimeout(() => {
    for (const client of clients) {
      client.close();
    }
  }, (rampSeconds + publishSeconds + 15) * 1000);
}
