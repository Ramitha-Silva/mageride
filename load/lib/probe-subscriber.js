// =====================================================================================
// C129 diagnostic — is the ingest ceiling EMQX's routing, or mqtt-bridge-svc's acknowledgement?
//
//   k6 run load/lib/probe-subscriber.js -e LOAD_CONNECTIONS=25 -e LOAD_RATE=4 -e LOAD_DURATION=30
//
// The step sweep showed EMQX delivering a flat ~13 msg/s to the bridge however fast the fleet
// publishes, with everything above that counted `delivery.dropped.queue_full`. Two very
// different causes produce that shape:
//
//   (a) the broker cannot route faster on this box — a sizing problem;
//   (b) the broker is waiting for PUBACKs. A QoS-1 subscriber may hold at most
//       `mqtt.max_inflight` (32) unacknowledged messages, and mqtt-bridge-svc acknowledges only
//       after its Redpanda produce completes, so its throughput is 32 / produce-round-trip and
//       everything beyond the 1,000-deep `max_mqueue_len` is discarded.
//
// This tells them apart. A second subscriber — a `svc-` principal, which `acl.conf` grants
// `veh/#` — takes the same firehose at QoS 0, where no inflight window applies and no PUBACK is
// owed. If it keeps up while the bridge does not, the ceiling is (b).
// =====================================================================================

import { Counter } from 'k6/metrics';
import { MqttClient } from './mqtt.js';
import { encodePosition } from './cbor.js';
import { mqttSessionToken } from './jwt.js';
import { config, requireConfigured } from './config.js';
import { Vehicle, positionTopic } from './fleet.js';

const connections = Number(__ENV.LOAD_CONNECTIONS || 25);
const ratePerConnection = Number(__ENV.LOAD_RATE || 4);
const durationSeconds = Number(__ENV.LOAD_DURATION || 30);

const publishedCount = new Counter('probe_published');
const receivedCount = new Counter('probe_received');

export const options = {
  scenarios: {
    probe: {
      executor: 'per-vu-iterations',
      vus: 1,
      iterations: 1,
      maxDuration: `${durationSeconds + 90}s`,
      gracefulStop: '20s',
    },
  },
  insecureSkipTLSVerify: true,
};

export function setup() {
  requireConfigured(['mqttUrl', 'mqttSecret']);
  return { startedAt: Date.now() };
}

export default function () {
  // `svc-` is the prefix acl.conf grants the wildcard to, and `verify_claims` still has to hold,
  // so the token's vehicleId claim is the username — exactly as MqttSessionTokenIssuer
  // .IssueForService mints it for mqtt-bridge-svc and tcp-adapter.
  const serviceName = 'svc-c129-probe';

  const subscriber = new MqttClient({
    url: config.mqttUrl,
    clientId: `${serviceName}-${Date.now()}`,
    username: serviceName,
    password: mqttSessionToken(serviceName, config.mqttSecret),
    keepAlive: 0,
    onOpen: (self) => {
      // QoS 0: no inflight window, no PUBACK owed. Whatever this misses, the broker chose not to
      // route rather than chose to wait.
      self.subscribe('veh/+/pos/live');
      console.log('probe subscriber joined veh/+/pos/live at QoS 0');
    },
    onMessage: () => receivedCount.add(1),
    onError: (message) => console.error(`subscriber: ${message}`),
  });

  const clients = [];

  // A second to let the SUBACK land before anything publishes.
  setTimeout(() => {
    for (let index = 0; index < connections; index++) {
      const vehicle = new Vehicle(index, {
        originLat: config.originLat,
        originLng: config.originLng,
      });

      const client = new MqttClient({
        url: config.mqttUrl,
        clientId: `probe-pub-${vehicle.id}`,
        username: vehicle.id,
        password: mqttSessionToken(vehicle.id, config.mqttSecret),
        keepAlive: 0,
        onOpen: (self) => {
          const topic = positionTopic(vehicle.id);
          const started = Date.now();
          let due = 0;

          const timer = setInterval(() => {
            const owed = Math.floor((Date.now() - started) / (1000 / ratePerConnection));
            let budget = 4;

            while (due < owed && budget-- > 0) {
              if (self.inFlight > 64) break;
              if (!self.publish(topic, encodePosition(vehicle.next()))) break;
              due++;
              publishedCount.add(1);
            }

            if (Date.now() - started > durationSeconds * 1000) {
              clearInterval(timer);
              self.close();
            }
          }, Math.min(1000 / ratePerConnection, 250));
        },
        onError: () => {},
      });

      clients.push(client);
    }
  }, 1000);

  setTimeout(() => {
    console.log(
      `published=${publishedCount.name ? 'see summary' : ''} subscriber received ` +
      `${subscriber.published === 0 ? '' : ''}(see probe_received in the summary)`);
    for (const client of clients) {
      client.close();
    }
    subscriber.close();
  }, (durationSeconds + 20) * 1000);
}
