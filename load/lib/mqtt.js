// =====================================================================================
// MQTT 3.1.1 over WebSocket, in the k6 runtime (C129).
//
// WHY THIS EXISTS RATHER THAN AN xk6 EXTENSION
// ------------------------------------------------------------------------------------
// k6 core speaks HTTP, gRPC and WebSocket. It does not speak MQTT, and the usual answer —
// `xk6 build --with .../xk6-mqtt` — produces a bespoke k6 binary that the verify command in
// build/manifest.yaml (`k6 run load/ingest.js`) would silently depend on: the same command
// against a stock k6 would fail at the import with a message about a module, not about a
// missing toolchain. So the protocol is written here, against the ONE listener a mobile
// publisher actually uses.
//
//   8084  MQTT over WSS   <- the driver app. JWT authentication, `websocket.mqtt_path = /mqtt`.
//   8883  MQTTS           <- hardware trackers, mutual TLS. NOT reachable this way and NOT
//                            driven by this suite; see load/README.md for what that costs.
//
// Everything below is the 3.1.1 wire format as `infra/deploy/emqx/emqx.conf` configures it:
// QoS 1 (the broker sets `max_qos_allowed = 1` and D6' §3.1 makes every topic QoS 1), a clean
// session, and the username/password pair emqx.conf's `verify_claims` binds together.
// =====================================================================================

import { WebSocket } from 'k6/websockets';

// MQTT control packet types, high nibble of byte 0.
const CONNECT = 0x10;
const CONNACK = 0x20;
const PUBLISH = 0x30;
const PUBACK = 0x40;
const SUBSCRIBE = 0x80;
const SUBACK = 0x90;
const PINGREQ = 0xc0;
const PINGRESP = 0xd0;
const DISCONNECT = 0xe0;

/** CONNACK return codes, so a refusal names itself rather than arriving as a number. */
const CONNACK_REASON = {
  0: 'accepted',
  1: 'unacceptable protocol version',
  2: 'client id rejected',
  3: 'server unavailable',
  4: 'bad username or password — the JWT did not validate against EMQX_AUTHENTICATION__1__SECRET',
  5: 'not authorised — the token\'s vehicleId claim does not equal the MQTT username (emqx.conf verify_claims)',
};

// -------------------------------------------------------------------------------------
// Encoding primitives
// -------------------------------------------------------------------------------------

/** UTF-8 bytes of a string. Topics and ids here are ASCII, but a payload never is. */
function utf8(text) {
  const out = [];
  for (let i = 0; i < text.length; i++) {
    let code = text.charCodeAt(i);
    if (code < 0x80) {
      out.push(code);
    } else if (code < 0x800) {
      out.push(0xc0 | (code >> 6), 0x80 | (code & 0x3f));
    } else if (code >= 0xd800 && code <= 0xdbff) {
      // Surrogate pair.
      const low = text.charCodeAt(++i);
      code = 0x10000 + ((code - 0xd800) << 10) + (low - 0xdc00);
      out.push(
        0xf0 | (code >> 18),
        0x80 | ((code >> 12) & 0x3f),
        0x80 | ((code >> 6) & 0x3f),
        0x80 | (code & 0x3f));
    } else {
      out.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 0x3f), 0x80 | (code & 0x3f));
    }
  }
  return out;
}

/** A length-prefixed MQTT string: two big-endian length bytes, then the UTF-8. */
function mqttString(text) {
  const bytes = utf8(text);
  return [(bytes.length >> 8) & 0xff, bytes.length & 0xff].concat(bytes);
}

/**
 * The remaining-length varint. Four bytes at most, which is the format's own ceiling of
 * 256 MB — a position sample is ~230 bytes, so this only ever emits one or two.
 */
function remainingLength(value) {
  const out = [];
  let left = value;
  do {
    let digit = left % 128;
    left = Math.floor(left / 128);
    if (left > 0) {
      digit |= 0x80;
    }
    out.push(digit);
  } while (left > 0);
  return out;
}

function packet(header, body) {
  return new Uint8Array([header].concat(remainingLength(body.length), body)).buffer;
}

// -------------------------------------------------------------------------------------
// Decoding — only the three packets a publisher has to understand
// -------------------------------------------------------------------------------------

/**
 * Splits a WebSocket frame into MQTT packets.
 *
 * A frame is not a packet: EMQX coalesces PUBACKs under load, and the very first frame of a
 * session regularly carries CONNACK and a PUBACK together. Treating the frame as one packet
 * loses every acknowledgement after the first and the publisher's in-flight window never
 * drains — which looks exactly like a broker that has stopped acknowledging.
 */
function decode(buffer) {
  const bytes = new Uint8Array(buffer);
  const packets = [];
  let at = 0;

  while (at < bytes.length) {
    const type = bytes[at] & 0xf0;
    let multiplier = 1;
    let length = 0;
    let cursor = at + 1;
    let digit;

    do {
      if (cursor >= bytes.length) {
        return packets; // Truncated. The rest arrives in the next frame.
      }
      digit = bytes[cursor++];
      length += (digit & 0x7f) * multiplier;
      multiplier *= 128;
    } while ((digit & 0x80) !== 0);

    if (cursor + length > bytes.length) {
      return packets;
    }

    packets.push({ type, body: bytes.subarray(cursor, cursor + length) });
    at = cursor + length;
  }

  return packets;
}

// -------------------------------------------------------------------------------------
// The client
// -------------------------------------------------------------------------------------

/**
 * One MQTT session over one WebSocket.
 *
 * Event-driven rather than await-driven on purpose: a k6 VU runs one event loop, and a client
 * that awaited each PUBACK would cap a whole VU at one broker round trip per publish — the
 * same mistake `TelemetryForwarder` records on the bridge side. `publish()` returns as soon as
 * the bytes are queued and the acknowledgement is counted on the callback.
 *
 * @param {object} options
 * @param {string} options.url        wss://host:8084/mqtt
 * @param {string} options.clientId   MQTT client id; two sessions sharing one disconnect each other
 * @param {string} options.username   the vehicleId (or svc-name) EMQX authorises against
 * @param {string} options.password   the session JWT
 * @param {number} options.keepAlive  seconds; 0 disables the broker's own liveness check
 * @param {function} options.onOpen   called once the CONNACK is accepted
 * @param {function} options.onAck    called with the packet id of each PUBACK
 * @param {function} options.onError  called with a message for any refusal or socket failure
 * @param {function} options.onMessage called with ({topic, payload}) for a delivered PUBLISH
 */
export function MqttClient(options) {
  const self = this;

  this.connected = false;
  this.published = 0;
  this.acknowledged = 0;
  this.inFlight = 0;
  this.errors = 0;

  let packetId = 0;
  let pinger = null;

  // Set by close(). A socket we are tearing down reports `close 1005 (no status)` through
  // onerror, and counting that as a broker error would put one failure per session into every
  // run's summary — enough to break a connect-success threshold at the burst profile's socket
  // count for a reason that is the generator shutting down.
  let closing = false;

  const socket = new WebSocket(options.url, ['mqtt']);
  socket.binaryType = 'arraybuffer';

  this.socket = socket;

  const fail = (message) => {
    if (closing) {
      return;
    }
    self.errors++;
    if (options.onError) {
      options.onError(message);
    }
  };

  socket.onopen = () => {
    const keepAlive = options.keepAlive === undefined ? 60 : options.keepAlive;

    const body = []
      .concat(mqttString('MQTT'))
      .concat([
        0x04, // protocol level 4 = MQTT 3.1.1
        0xc2, // username + password + clean session
        (keepAlive >> 8) & 0xff,
        keepAlive & 0xff,
      ])
      .concat(mqttString(options.clientId))
      .concat(mqttString(options.username))
      .concat(mqttString(options.password));

    socket.send(packet(CONNECT, body));

    if (keepAlive > 0) {
      // Half the interval, so a missed tick is not a disconnect. EMQX closes a session that
      // stays quiet for 1.5x the keep-alive it was given at CONNECT.
      pinger = setInterval(() => {
        if (self.connected) {
          socket.send(packet(PINGREQ, []));
        }
      }, (keepAlive * 1000) / 2);
    }
  };

  socket.onmessage = (event) => {
    for (const frame of decode(event.data)) {
      switch (frame.type) {
        case CONNACK: {
          const code = frame.body[1];
          if (code === 0) {
            self.connected = true;
            if (options.onOpen) {
              options.onOpen(self);
            }
          } else {
            fail(`CONNACK ${code}: ${CONNACK_REASON[code] || 'refused'}`);
            self.close();
          }
          break;
        }

        case PUBACK: {
          self.acknowledged++;
          self.inFlight--;
          if (options.onAck) {
            options.onAck((frame.body[0] << 8) | frame.body[1]);
          }
          break;
        }

        case SUBACK: {
          // 0x80 is the failure code; anything else is the granted QoS.
          if (frame.body[2] === 0x80) {
            fail('SUBACK refused the subscription (acl.conf denies this username the filter)');
          }
          break;
        }

        case PUBLISH: {
          if (options.onMessage) {
            const topicLength = (frame.body[0] << 8) | frame.body[1];
            const qos = 0; // Only QoS 0 downlink is subscribed to here; no packet id to skip.
            const start = 2 + topicLength + qos;
            let topic = '';
            for (let i = 2; i < 2 + topicLength; i++) {
              topic += String.fromCharCode(frame.body[i]);
            }
            options.onMessage({ topic, payload: frame.body.subarray(start) });
          }
          break;
        }

        case PINGRESP:
          break;

        default:
          break;
      }
    }
  };

  socket.onerror = (event) => fail(`socket error: ${event && event.error ? event.error : 'unknown'}`);

  socket.onclose = () => {
    self.connected = false;
    if (pinger !== null) {
      clearInterval(pinger);
      pinger = null;
    }
  };

  /**
   * Publishes at QoS 1 — the level D6' §3.1 gives every topic in the tree and the only one
   * that produces the PUBACK this suite counts deliveries with.
   */
  this.publish = function publish(topic, payload) {
    if (!self.connected) {
      return false;
    }

    packetId = (packetId % 65535) + 1;

    const head = mqttString(topic).concat([(packetId >> 8) & 0xff, packetId & 0xff]);
    const body = new Uint8Array(head.length + payload.length);
    body.set(head, 0);
    body.set(payload, head.length);

    // 0x32 = PUBLISH | QoS 1. Not retained: `pos/live`'s retain-last is the broker's own
    // setting for the device plane, and a retained flood would leave one message per vehicle
    // on the broker for every run of this suite.
    socket.send(packet(PUBLISH | 0x02, Array.from(body)));

    self.published++;
    self.inFlight++;
    return true;
  };

  this.subscribe = function subscribe(topic) {
    packetId = (packetId % 65535) + 1;
    const body = [(packetId >> 8) & 0xff, packetId & 0xff].concat(mqttString(topic), [0x00]);
    socket.send(packet(SUBSCRIBE, body));
  };

  this.close = function close() {
    closing = true;
    if (pinger !== null) {
      clearInterval(pinger);
      pinger = null;
    }
    try {
      if (self.connected) {
        socket.send(packet(DISCONNECT, []));
      }
      socket.close();
    } catch (error) {
      // A socket the broker has already dropped. Nothing to report — the counters are what
      // this suite measures and they are already recorded.
    }
    self.connected = false;
  };
}
