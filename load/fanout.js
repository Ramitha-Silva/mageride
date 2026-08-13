// =====================================================================================
// C129 — ADD §16.3's fan-out cost model, measured.
//
//   k6 run load/fanout.js                       # 30 subscribers per cell, §16.3's own figure
//   k6 run load/fanout.js -e SUBSCRIBERS_PER_CELL=10 -e CELLS=20
//
// ------------------------------------------------------------------------------------
// THE UNIT §16.3 COUNTS, AND THE UNIT THE PLATFORM COUNTS
// ------------------------------------------------------------------------------------
// §16.3 prices fan-out in **SignalR sends per pod per second**:
//
//     3,000 active vehicles x 0.12 Hz x 30 subscribers per cell = ~10,800 sends/s
//     10,800 / 10,000 sends per pod per second (D-40's lower bound) = 1-2 pods, run 3 for HA
//
// A "send" there is one message delivered to one subscriber. The platform's own counter,
// `mageride.fanout.frames`, is not that number: `CellStreamPump` calls
// `Clients.Group(cell).SendAsync("VehiclePositions", visible)` **once per cell per tick** and then
// adds `visible.Count` — so it counts VEHICLE FRAMES per group send and is completely independent
// of how many subscribers are in the group. A cell with one subscriber and a cell with a thousand
// produce the same reading.
//
// So the number §16.3's pod arithmetic needs cannot be read off the deployment, and this profile
// measures it the only way left: by counting, on the client side, the WebSocket messages that
// actually arrived. Every subscriber here is a real `/hubs/live` connection holding real geocell
// group memberships, so the count is the delivered-message count by construction.
//
// The finding — that the counter an operator would scale on is not the quantity the model is
// written in — is in load/report.md.
// =====================================================================================

import { Counter, Trend, Rate } from 'k6/metrics';
import { MqttClient } from './lib/mqtt.js';
import { encodePosition } from './lib/cbor.js';
import { mqttSessionToken } from './lib/jwt.js';
import { config, requireConfigured } from './lib/config.js';
import { Vehicle, positionTopic } from './lib/fleet.js';
import { LiveHubClient } from './lib/signalr.js';

// §16.3's own shape: ~5 vehicles per 3 km cell, ~30 passengers per cell.
const cells = Number(__ENV.CELLS || 20);
const subscribersPerCell = Number(__ENV.SUBSCRIBERS_PER_CELL || 30);
const vehiclesPerCell = Number(__ENV.VEHICLES_PER_CELL || 5);

const durationSeconds = Number(__ENV.LOAD_DURATION || 120);
const rampSeconds = Math.max(10, Math.ceil((cells * subscribersPerCell) / 60));

// The vehicles publish at 4 Hz, not at §16.1's blended 0.12 Hz. The fan-out batch carries the
// NEWEST frame per vehicle per 2 s tick, so anything at or above 0.5 Hz produces one frame per
// vehicle per batch and the send count is identical — while a faster publisher keeps the cell
// stream warm and the freshness filter satisfied. What the publish rate would change is the
// INGEST cost, which is load/ingest.js's subject.
const publishRate = Number(__ENV.LOAD_RATE || 4);

const subscriberVus = Math.max(1, Math.min(120, Math.ceil((cells * subscribersPerCell) / 40)));
const publisherVus = Math.max(1, Math.ceil((cells * vehiclesPerCell) / 25));

const hubMessages = new Counter('fanout_hub_messages');
const hubFrames = new Counter('fanout_hub_frames');
const hubReady = new Rate('fanout_hub_ready');
const hubConnectMs = new Trend('fanout_hub_connect_ms', true);
const subscribers = new Counter('fanout_subscribers');

export const options = {
  scenarios: {
    subscribers: {
      executor: 'per-vu-iterations',
      exec: 'subscriber',
      vus: subscriberVus,
      iterations: 1,
      maxDuration: `${rampSeconds + durationSeconds + 90}s`,
      gracefulStop: '30s',
    },
    publishers: {
      executor: 'per-vu-iterations',
      exec: 'publisher',
      vus: publisherVus,
      iterations: 1,
      startTime: `${rampSeconds}s`,
      maxDuration: `${durationSeconds + 90}s`,
      gracefulStop: '30s',
    },
  },
  insecureSkipTLSVerify: true,
  thresholds: {
    fanout_hub_ready: ['rate>0.95'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'max'],
};

/** The cells the fixture actually placed vehicles in, most populated first. */
function chosenCells() {
  const byCell = {};

  for (const [index, cell] of Object.entries(config.cellsByVehicle)) {
    (byCell[cell] = byCell[cell] || []).push(Number(index));
  }

  return Object.entries(byCell)
    .filter(([, members]) => members.length >= vehiclesPerCell)
    .sort((a, b) => b[1].length - a[1].length)
    .slice(0, cells)
    .map(([cell, members]) => ({ cell, vehicles: members.slice(0, vehiclesPerCell) }));
}

export function setup() {
  requireConfigured(['mqttUrl', 'mqttSecret', 'watcherToken', 'cellsByVehicle']);

  const chosen = chosenCells();

  if (chosen.length === 0) {
    throw new Error(
      `No res-7 cell in load/env.json holds ${vehiclesPerCell} vehicles. Re-run ` +
      '`bash load/configure.sh --fleet 750`, or lower VEHICLES_PER_CELL.');
  }

  console.log(
    `fan-out: ${chosen.length} cells x ${subscribersPerCell} subscribers, ` +
    `${chosen.length * vehiclesPerCell} vehicles at ${publishRate} Hz. ` +
    `ADD §16.3's model predicts ${chosen.length * vehiclesPerCell * subscribersPerCell / 2} ` +
    'sends/s at a 2 s batch interval.');

  return { chosen, startedAt: Date.now() };
}

export function subscriber(plan) {
  const total = plan.chosen.length * subscribersPerCell;
  const perVu = Math.ceil(total / subscriberVus);
  const first = (__VU - 1) * perVu;
  const clients = [];

  for (let n = first; n < Math.min(total, first + perVu); n++) {
    const cell = plan.chosen[n % plan.chosen.length].cell;
    const startedConnect = Date.now();

    // Staggered: `/hubs/live` goes straight to fanout-svc through HAProxy (the `is_hub` ACL), so
    // a thousand simultaneous upgrades would be measuring the accept queue.
    setTimeout(() => {
      const hub = new LiveHubClient({
        edge: config.edge,
        token: config.watcherToken,
        onReady: (self) => {
          hubConnectMs.add(Date.now() - startedConnect);
          hubReady.add(true);
          subscribers.add(1);
          self.joinGeocells([cell]);
        },
        onEvent: (target, args) => {
          if (target !== 'VehiclePositions') {
            return;
          }
          // ONE hub message = one §16.3 "send". The frames inside it are what the platform's own
          // `mageride.fanout.frames` counter counts, and the two differ by the group's size.
          hubMessages.add(1);
          hubFrames.add((args[0] || []).length);
        },
        onError: () => hubReady.add(false),
      });

      clients.push(hub);
    }, ((n - first) / Math.max(1, perVu)) * rampSeconds * 1000);
  }

  setTimeout(() => {
    for (const hub of clients) {
      hub.close();
    }
  }, (rampSeconds + durationSeconds + 20) * 1000);
}

export function publisher(plan) {
  const wanted = plan.chosen.flatMap((entry) => entry.vehicles);
  const perVu = Math.ceil(wanted.length / publisherVus);
  const first = (__VU - 1) * perVu;
  const clients = [];

  for (const index of wanted.slice(first, first + perVu)) {
    const vehicle = new Vehicle(index, {
      originLat: config.originLat,
      originLng: config.originLng,
    });

    const client = new MqttClient({
      url: config.mqttUrl,
      clientId: `fanout-${vehicle.id}`,
      username: vehicle.id,
      password: mqttSessionToken(vehicle.id, config.mqttSecret),
      keepAlive: 0,
      onOpen: (self) => {
        const topic = positionTopic(vehicle.id);
        const started = Date.now();
        let due = 0;

        const timer = setInterval(() => {
          const owed = Math.floor((Date.now() - started) / (1000 / publishRate));
          let budget = 4;

          while (due < owed && budget-- > 0) {
            if (self.inFlight > 64) break;
            if (!self.publish(topic, encodePosition(vehicle.next()))) break;
            due++;
          }

          if (Date.now() - started > durationSeconds * 1000) {
            clearInterval(timer);
            self.close();
          }
        }, Math.min(1000 / publishRate, 250));
      },
      onError: () => {},
    });

    clients.push(client);
  }

  setTimeout(() => {
    for (const client of clients) {
      client.close();
    }
  }, (durationSeconds + 15) * 1000);
}

export function handleSummary(data) {
  const count = (name) => {
    const metric = data.metrics[name];
    return metric && metric.values ? metric.values.count || 0 : 0;
  };
  const trend = (name, stat) => {
    const metric = data.metrics[name];
    return metric && metric.values ? metric.values[stat] : undefined;
  };

  const messages = count('fanout_hub_messages');
  const held = count('fanout_subscribers');

  const result = {
    component: 'C129',
    profile: 'fanout',
    plan: {
      cells,
      subscribersPerCell,
      vehiclesPerCell,
      durationSeconds,
      modelSendsPerSecond: (cells * vehiclesPerCell * subscribersPerCell) / 2,
    },
    measured: {
      subscribersHeld: held,
      hubMessages: messages,
      hubFrames: count('fanout_hub_frames'),
      sendsPerSecond: Number((messages / durationSeconds).toFixed(1)),
      framesPerSend:
        messages > 0 ? Number((count('fanout_hub_frames') / messages).toFixed(2)) : 0,
      hubConnectMs: {
        med: trend('fanout_hub_connect_ms', 'med'),
        p95: trend('fanout_hub_connect_ms', 'p(95)'),
        max: trend('fanout_hub_connect_ms', 'max'),
      },
    },
    note:
      'sendsPerSecond is the §16.3 unit — WebSocket messages delivered to subscribers. The ' +
      'platform\'s mageride.fanout.frames counter is frames per GROUP send and does not vary ' +
      'with subscriber count; load/collect.sh records it separately for the comparison.',
  };

  return {
    stdout:
      `\n  C129 fan-out\n` +
      `  subscribers   ${held} held over ${cells} cells\n` +
      `  sends         ${result.measured.sendsPerSecond}/s measured against ` +
      `${result.plan.modelSendsPerSecond}/s modelled (ADD §16.3)\n` +
      `  batch         ${result.measured.framesPerSend} vehicle frames per send\n` +
      `  hub connect   med ${(result.measured.hubConnectMs.med || 0).toFixed(0)} ms  ` +
      `p95 ${(result.measured.hubConnectMs.p95 || 0).toFixed(0)} ms\n\n`,
    'load/out/fanout.json': JSON.stringify(result, null, 2),
  };
}
