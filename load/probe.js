// A one-vehicle, one-publish smoke of the MQTT-over-WSS path. Not part of the suite's
// deliverables — it is the fastest way to tell a broken JWT from a broken listener from a
// broken codec, and `load/run.sh` calls it before every profile.
import { WebSocket } from 'k6/experimental/websockets';
import { MqttClient } from './lib/mqtt.js';
import { encodePosition } from './lib/cbor.js';
import { mqttSessionToken } from './lib/jwt.js';
import { config, requireConfigured } from './lib/config.js';
import { Vehicle, positionTopic } from './lib/fleet.js';

export const options = {
  vus: 1,
  iterations: 1,
  insecureSkipTLSVerify: true,
};

export function setup() {
  requireConfigured(['mqttUrl', 'mqttSecret']);
}

export default function () {
  const vehicle = new Vehicle(0, { originLat: config.originLat, originLng: config.originLng });
  const sample = vehicle.next();
  const payload = encodePosition(sample);

  console.log(`payload is ${payload.length} bytes of CBOR (ADD §3.4 A3 assumes 80–120)`);

  const client = new MqttClient({
    url: config.mqttUrl,
    clientId: `load-probe-${Date.now()}`,
    username: vehicle.id,
    password: mqttSessionToken(vehicle.id, config.mqttSecret),
    keepAlive: 30,
    onOpen: () => {
      console.log('CONNACK accepted');
      client.publish(positionTopic(vehicle.id), payload);
    },
    onAck: () => {
      console.log('PUBACK — the broker accepted the publish on the vehicle\'s own topic');
      client.close();
    },
    onError: (message) => {
      console.error(`refused: ${message}`);
      client.close();
    },
  });

  setTimeout(() => {
    if (client.acknowledged === 0) {
      console.error(`no PUBACK after 10 s (connected=${client.connected}, published=${client.published})`);
    }
    client.close();
  }, 10000);
}
