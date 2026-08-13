// =====================================================================================
// The `/hubs/live` client, in the k6 runtime (C129).
//
// ASP.NET Core SignalR's JSON protocol over a raw WebSocket — the same three steps the
// official client performs when `skipNegotiation` is set: open the socket, exchange the
// handshake, then send and receive newline-free JSON records separated by 0x1E.
//
// WHY NEGOTIATION IS SKIPPED
// ------------------------------------------------------------------------------------
// `POST /hubs/live/negotiate` exists to choose a transport and to hand out a connection token
// for the fallback transports. This suite only ever wants WebSockets, and the negotiate round
// trip would put one authenticated POST through the gateway per subscriber — 4,500 of them at
// the §16.3 subscriber scale, which is a burst of API load inside a fan-out measurement. The
// server accepts a direct WebSocket connection to the hub path, which is what the real client
// does under `skipNegotiation: true`.
//
// THE CREDENTIAL IS THE API ACCESS TOKEN, IN A QUERY PARAMETER
// ------------------------------------------------------------------------------------
// `Fanout.Api/CLAUDE.md`: the ordinary 30-minute API token (D-29), never the MQTT session JWT
// (E-02), and in `access_token` rather than a header because a browser `WebSocket` cannot set
// one. The kernel's query hook is scoped to `/hubs/live` for that reason.
// =====================================================================================

import { WebSocket } from 'k6/websockets';

/** SignalR's record separator. Every frame — including the handshake — ends with it. */
const RS = String.fromCharCode(0x1e);

const INVOCATION = 1;
const PING = 6;
const CLOSE = 7;

/**
 * One `/hubs/live` connection.
 *
 * @param {object} options
 * @param {string} options.edge      https://host[:port] — the same edge every other caller uses
 * @param {string} options.token     the API access token
 * @param {function} options.onReady called once the handshake completes
 * @param {function} options.onEvent called with (target, args) for every server invocation
 * @param {function} options.onError called with a message
 */
export function LiveHubClient(options) {
  const self = this;

  this.ready = false;
  this.received = 0; // WebSocket messages carrying a hub invocation — §16.3's "SignalR send".
  this.frames = 0; // Vehicle frames inside them.
  this.errors = 0;

  const url = `${options.edge.replace(/^http/, 'ws')}/hubs/live?access_token=${options.token}`;
  const socket = new WebSocket(url);
  let pinger = null;
  let partial = '';

  this.socket = socket;

  const fail = (message) => {
    self.errors++;
    if (options.onError) {
      options.onError(message);
    }
  };

  socket.onopen = () => {
    socket.send(`{"protocol":"json","version":1}${RS}`);
  };

  socket.onmessage = (event) => {
    // A WebSocket frame is not a hub message: SignalR coalesces records, and the handshake
    // response regularly arrives glued to the first invocation. Splitting on the separator —
    // and keeping the trailing fragment — is the protocol's own framing.
    const text = partial + event.data;
    const records = text.split(RS);
    partial = records.pop();

    for (const record of records) {
      if (record.length === 0) {
        continue;
      }

      let message;
      try {
        message = JSON.parse(record);
      } catch (error) {
        fail(`unparseable hub record: ${record.slice(0, 120)}`);
        continue;
      }

      if (!self.ready) {
        // The handshake response is the only record with no `type`: `{}` on success,
        // `{"error": "..."}` on refusal.
        if (message.error) {
          fail(`handshake refused: ${message.error}`);
          self.close();
          return;
        }

        self.ready = true;

        // 15 s, comfortably inside SignalR's 30 s default client timeout. A hub connection is
        // silent between position pushes and HAProxy's `fanout` backend raises its own server
        // timeout for exactly that reason.
        pinger = setInterval(() => self.send({ type: PING }), 15000);

        if (options.onReady) {
          options.onReady(self);
        }
        continue;
      }

      if (message.type === INVOCATION) {
        self.received++;
        if (options.onEvent) {
          options.onEvent(message.target, message.arguments || []);
        }
      } else if (message.type === CLOSE) {
        fail(`hub closed the connection: ${message.error || 'no reason given'}`);
        self.close();
        return;
      }
    }
  };

  socket.onerror = (event) => fail(`socket error: ${event && event.error ? event.error : 'unknown'}`);

  socket.onclose = () => {
    self.ready = false;
    if (pinger !== null) {
      clearInterval(pinger);
      pinger = null;
    }
  };

  this.send = function send(message) {
    socket.send(JSON.stringify(message) + RS);
  };

  /**
   * `JoinGeocells` — res-7 ids only.
   *
   * The hub throws a `HubException` naming the resolution for anything else, and a connection
   * that holds more than `Fanout:MaxCellsPerConnection` (128) is refused outright, so a caller
   * splitting a large set across connections is doing what the contract expects.
   */
  this.joinGeocells = function joinGeocells(cells) {
    self.send({ type: INVOCATION, target: 'JoinGeocells', arguments: [cells] });
  };

  this.close = function close() {
    if (pinger !== null) {
      clearInterval(pinger);
      pinger = null;
    }
    try {
      socket.close();
    } catch (error) {
      // Already gone. The counters are the measurement and they are already recorded.
    }
    self.ready = false;
  };
}

/**
 * The key both sides of the latency measurement agree on.
 *
 * `VehiclePositions` carries `{vehicleId, lat, lng, heading, speed, type, mode}` and **no
 * timestamp** (`signalr-hub.md` §3), so a subscriber cannot compute the D-19 delay from the
 * frame alone. What it can do is recognise a position it published itself: the fleet moves in
 * 10 m steps, so seven decimal places (~1 cm) identify one sample of one vehicle uniquely, and
 * formatting both sides through `toFixed` removes any disagreement about how a double prints.
 */
export function frameKey(vehicleId, lat, lng) {
  return `${vehicleId}|${lat.toFixed(7)}|${lng.toFixed(7)}`;
}
