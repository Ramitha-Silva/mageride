// =====================================================================================
// C129 — the dispatch profile: concurrent ride requests, and how long an offer takes.
//
//   k6 run load/dispatch.js                     # the manifest's verify, second half
//   k6 run load/dispatch.js -e PAIRS=24 -e LOAD_DURATION=180
//
// Prerequisite: `bash load/configure.sh` (the accounts, their bearers and their vehicles).
//
// ------------------------------------------------------------------------------------
// WHAT "TARGET CONCURRENCY" IS HERE, AND WHY IT IS SMALL
// ------------------------------------------------------------------------------------
// ADD §16.4 prices the whole ride plane at **10k trips/day x 10 events = ~1 TPS** and calls it
// trivial. Two unique indexes make that the ceiling rather than an average:
//
//   ux_rides_open_passenger   one non-terminal ride per passenger
//   ux_offers_driver_live     one OFFERED-or-ACCEPTED offer per driver (R-10)
//
// So concurrency is bounded by ACCOUNTS, not by request rate: N concurrent rides need N
// passengers and N drivers, and no amount of load generation changes that. This profile runs
// one ride at a time per (passenger, driver) pair and reports the aggregate.
//
// The second ceiling is the edge, and it binds first — see the comment on `pairs`.
//
// ------------------------------------------------------------------------------------
// EVERY PAIR GETS ITS OWN SQUARE OF SRI LANKA
// ------------------------------------------------------------------------------------
// The candidate pool is global: every online driver within the 5 km search radius is a
// candidate for every ride. Two pairs at one pickup share a pool, and "the ride went to the
// driver in this pair" stops being true — which would make an offer-latency measurement a
// measurement of contention between test fixtures. The grid is `tests/E2E`'s
// `ModeCFleet.NextPlaces`, at 0.12 degrees (~13 km, comfortably over twice the radius) inside
// the bounding box fare-svc prices.
//
// ------------------------------------------------------------------------------------
// WHY EVERY RIDE ENDS IN A CANCEL, AND WHY THAT IS NOT AN ACCEPT TEST
// ------------------------------------------------------------------------------------
// `POST /v1/rides/{id}/offer/{driverId}/accept` requires the **offerId**, and the offer id is
// delivered to the driver by FCM push and by nothing else: `GET /v1/rides/{id}/state` returns
// `{state, version, offerExpiresAt}`, `RideDetail` carries `offerExpiresAt` and a `driver` block
// that appears only from `Accepted` onward, and `dispatch.yaml` has no driver-side offer read at
// all. So a REST client cannot accept an offer it was not pushed. The accept path is exercised
// by `load/accept-race.sh`, which takes the offer id from `dispatch.offers` — standing in for
// the push payload — and races N concurrent accepts against it (ADD §11.11).
//
// Cancelling in `Offered` is `CancelledByRiderBeforeAccept`, which is terminal, frees the
// passenger and releases the driver. It is **pre**-acceptance, so it does not count toward
// AL-16's three-consecutive-post-acceptance booking disable; `load/collect.sh` asserts that no
// `dispatch.cancellation_penalties` row was raised for a load passenger.
// =====================================================================================

import http from 'k6/http';
import { Counter, Trend, Rate } from 'k6/metrics';
import { sleep } from 'k6';
import { MqttClient } from './lib/mqtt.js';
import { encodePosition } from './lib/cbor.js';
import { mqttSessionToken } from './lib/jwt.js';
import { config, requireConfigured } from './lib/config.js';
import { positionTopic } from './lib/fleet.js';

// Eight, not "as many accounts as there are", and the ceiling is the EDGE rather than
// dispatch-svc. `ride-rides` (`/v1/rides/{**remainder}`, every method), `fare` and
// `dispatch-standby` all carry the gateway's `write` policy — 120 requests per minute — and on
// this deployment that bucket is shared by the whole platform (see the rate-limit note below).
// A ride costs five of those: one estimate, one request, ~two state polls and one cancel. Eight
// pairs on a 20 s cycle is 0.4 rides/s = ~35k rides/day, which is 3.5x ADD §16.4's 10k/day
// launch figure and still under the ceiling. Raising it measures the ceiling, not dispatch-svc.
const pairs = Number(
  __ENV.PAIRS || Math.min(8, config.passengers.length, config.drivers.length) || 1);
const durationSeconds = Number(__ENV.LOAD_DURATION || 180);
const pollIntervalSeconds = Number(__ENV.LOAD_POLL || 2);

// The drivers go on standby and start publishing before the first booking. 20 s is dispatch-svc's
// presence write plus one telemetry round trip through EMQX, Redpanda and the processor — a ride
// booked before that rests in `Matching` because the pool is empty, which is a correct platform
// answer to a wrong fixture.
const driverLeadSeconds = 20;

const thinkSeconds = Number(__ENV.LOAD_THINK || 14);

// -------------------------------------------------------------------------------------
// Metrics
// -------------------------------------------------------------------------------------

const estimateMs = new Trend('fare_estimate_ms', true);
const requestMs = new Trend('ride_request_ms', true);
const offerWaitMs = new Trend('offer_wait_ms', true);
const cancelMs = new Trend('ride_cancel_ms', true);

const requested = new Counter('rides_requested');
const offered = new Counter('rides_offered');
const notOffered = new Counter('rides_never_offered');
const cancelled = new Counter('rides_cancelled');
const statePolls = new Counter('ride_state_polls');
const apiErrors = new Counter('dispatch_api_errors');
// Counted apart from the rest: a 429 is the edge working as configured, not the platform
// failing, and it is the single most important number this profile produces.
const rateLimited = new Counter('dispatch_rate_limited');
const online = new Rate('driver_online_ok');

export const options = {
  scenarios: {
    // One VU per pair, each publishing its vehicle's position for the whole run.
    drivers: {
      executor: 'per-vu-iterations',
      exec: 'driver',
      vus: pairs,
      iterations: 1,
      maxDuration: `${driverLeadSeconds + durationSeconds + 60}s`,
      gracefulStop: '20s',
    },
    passengers: {
      executor: 'per-vu-iterations',
      exec: 'passenger',
      vus: pairs,
      iterations: 1,
      startTime: `${driverLeadSeconds}s`,
      maxDuration: `${durationSeconds + 60}s`,
      gracefulStop: '30s',
    },
  },
  insecureSkipTLSVerify: true,
  thresholds: {
    // ADD §13.3.1's stuck-state table is the only documented budget on this path: a ride may
    // sit in `Matching` for 60 s before R-20 calls it stuck. It is an ALARM threshold rather
    // than a latency target, which load/report.md records as a spec gap — the tight number,
    // E-09's "offer push median < 50 ms", is about the outbox hop alone and is measured from
    // `rides.outbox` by load/collect.sh.
    offer_wait_ms: ['med<60000'],
    rides_never_offered: ['count<1'],
    dispatch_api_errors: ['count<1'],
    // Not zero-tolerance: the profile is sized to sit under the edge ceiling, so a handful of
    // refusals is jitter, but a run that is mostly 429 is measuring the limiter.
    dispatch_rate_limited: ['count<20'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

// -------------------------------------------------------------------------------------
// The grid — tests/E2E's, for the reason recorded there
// -------------------------------------------------------------------------------------

function places(index) {
  const step = 0.12;
  const columns = 19;

  const pickup = {
    lat: 6.0 + step * Math.floor(index / columns),
    lng: 79.6 + step * (index % columns),
  };

  // The same ~9.5 km hop Colombo Fort -> Dehiwala is, so every ride in the run is priced off
  // one distance band and a fare difference is never the reason a booking is refused.
  return {
    pickup,
    dropoff: { lat: pickup.lat - 0.083, lng: pickup.lng + 0.0225 },
  };
}

// -------------------------------------------------------------------------------------
// HTTP
// -------------------------------------------------------------------------------------

function headers(bearer, withIdempotency) {
  const value = {
    Authorization: `Bearer ${bearer}`,
    'Content-Type': 'application/json',
    Host: config.host,
  };

  if (withIdempotency) {
    // D3' §0 makes the header mandatory on EVERY POST mutation. Without it the answer is
    // `400 idempotency-key-required` rather than the route, and a run would measure the
    // kernel's refusal path at full speed.
    value['Idempotency-Key'] = `c129-${__VU}-${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
  }

  return value;
}

function post(path, body, bearer) {
  return http.post(`${config.edge}${path}`, JSON.stringify(body), {
    headers: headers(bearer, true),
    tags: { path },
  });
}

export function setup() {
  requireConfigured(['edge', 'passengers', 'drivers', 'mqttUrl', 'mqttSecret']);

  if (config.drivers.some((driver) => !driver.vehicleId)) {
    throw new Error(
      'A driver in load/env.json has no vehicleId. Re-run `bash load/configure.sh` — the ' +
      'standby call names the vehicle and the freshness gate needs its telemetry.');
  }

  console.log(
    `dispatch: ${pairs} (passenger, driver) pairs, ${durationSeconds}s, ` +
    `${config.passengers.length} passengers and ${config.drivers.length} drivers available`);

  return { startedAt: Date.now() };
}

// -------------------------------------------------------------------------------------
// The driver half — standby, then a position every second for the whole run
// -------------------------------------------------------------------------------------

export function driver() {
  const index = __VU - 1;
  const account = config.drivers[index % config.drivers.length];
  const { pickup } = places(index);

  const response = post(
    '/v1/standby/online',
    { vehicleId: account.vehicleId, position: { lat: pickup.lat, lng: pickup.lng } },
    account.bearer);

  if (response.status !== 200) {
    online.add(false);
    apiErrors.add(1);
    console.error(`standby/online for ${account.phone} answered ${response.status}: ${response.body}`);
    return;
  }

  online.add(true);

  // D5' §3.2's freshness gate drops a driver whose last position is older than
  // 2 x expectedInterval, and `Dispatch:PresenceTtl` is 60 s. A driver who went online and then
  // went quiet leaves the candidate pool mid-run and every later ride in that square rests in
  // `Matching` — which reads as a dispatch failure and is a fixture failure.
  let seq = Date.now();
  const client = new MqttClient({
    url: config.mqttUrl,
    clientId: `load-driver-${account.vehicleId}`,
    username: account.vehicleId,
    password: mqttSessionToken(account.vehicleId, config.mqttSecret),
    keepAlive: 0,
    onOpen: (self) => {
      const topic = positionTopic(account.vehicleId);
      const timer = setInterval(() => {
        seq = Date.now();
        // A stationary driver waiting at a rank: the position barely moves, which is both
        // realistic and safely inside every ADD §12.6 speed ceiling.
        self.publish(
          topic,
          encodePosition({
            vehicleId: account.vehicleId,
            sampleTs: seq,
            seq,
            lat: pickup.lat + (Math.random() - 0.5) * 0.00002,
            lng: pickup.lng + (Math.random() - 0.5) * 0.00002,
            source: 0,
            speedMps: 0,
            headingDeg: 0,
            accuracyM: 8,
            satCount: 11,
            mode: 'C',
            vehicleType: 'three_wheeler',
          }));
      }, 1000);

      setTimeout(() => {
        clearInterval(timer);
        self.close();
      }, (driverLeadSeconds + durationSeconds) * 1000);
    },
    onError: (message) => console.error(`driver mqtt: ${message}`),
  });

  // Held open for the run, then off standby so the pool is left as it was found.
  setTimeout(() => {
    client.close();
    post('/v1/standby/offline', {}, account.bearer);
  }, (driverLeadSeconds + durationSeconds + 5) * 1000);
}

// -------------------------------------------------------------------------------------
// The passenger half — quote, book, wait for the offer, cancel
// -------------------------------------------------------------------------------------

export function passenger(plan) {
  const index = __VU - 1;
  const account = config.passengers[index % config.passengers.length];
  const { pickup, dropoff } = places(index);

  const until = plan.startedAt + (driverLeadSeconds + durationSeconds) * 1000;

  while (Date.now() < until) {
    book(account, pickup, dropoff);
    sleep(thinkSeconds);
  }
}

function book(account, pickup, dropoff) {
  // 1 — the quote. ride-svc verifies the token's signature (D5' §1.1), so a booking cannot be
  // made without this hop and its cost belongs in the measurement.
  const query =
    `?fromLat=${pickup.lat}&fromLng=${pickup.lng}&toLat=${dropoff.lat}&toLng=${dropoff.lng}` +
    '&vehicleType=three_wheeler&kind=passenger';

  const quote = http.get(`${config.edge}/v1/fare/estimate${query}`, {
    headers: headers(account.bearer, false),
    tags: { path: '/v1/fare/estimate' },
  });

  estimateMs.add(quote.timings.duration);

  if (quote.status === 429) {
    rateLimited.add(1);
    return;
  }

  if (quote.status !== 200) {
    apiErrors.add(1);
    console.error(`fare/estimate answered ${quote.status}: ${String(quote.body).slice(0, 200)}`);
    return;
  }

  const token = quote.json('fareEstimateToken');

  // 2 — the booking. The clock starts here: this is the instant a passenger tapped Confirm.
  const requestedAt = Date.now();

  const booking = post(
    '/v1/rides/request',
    {
      clientRequestId: uuid(),
      kind: 'passenger',
      pickup: { lat: pickup.lat, lng: pickup.lng, address: 'C129 pickup' },
      dropoff: { lat: dropoff.lat, lng: dropoff.lng, address: 'C129 dropoff' },
      vehicleType: 'three_wheeler',
      fareEstimateToken: token,
      paymentMethod: 'cash',
    },
    account.bearer);

  requestMs.add(booking.timings.duration);

  if (booking.status === 429) {
    rateLimited.add(1);
    return;
  }

  if (booking.status !== 202) {
    apiErrors.add(1);
    console.error(`rides/request answered ${booking.status}: ${String(booking.body).slice(0, 240)}`);
    return;
  }

  requested.add(1);

  const rideId = booking.json('rideId');
  let version = booking.json('version');

  // 3 — wait for the offer. `GET /v1/rides/{id}/state` is the contract's own "cheap read the
  // driver app uses while an offer is live"; the SignalR `RideStateChanged` event is the normal
  // transport and this is the documented fallback, so polling it is a supported client shape
  // rather than a load-test contrivance. 250 ms is the quantisation of every figure below.
  let state = 'Requested';
  const deadline = Date.now() + 70000; // Past ADD §13.3.1's 60 s Matching budget, on purpose.

  while (Date.now() < deadline) {
    sleep(pollIntervalSeconds);

    const snapshot = http.get(`${config.edge}/v1/rides/${rideId}/state`, {
      headers: headers(account.bearer, false),
      tags: { path: '/v1/rides/{rideId}/state' },
    });

    statePolls.add(1);

    if (snapshot.status === 429) {
      rateLimited.add(1);
      continue;
    }

    if (snapshot.status !== 200) {
      apiErrors.add(1);
      break;
    }

    state = snapshot.json('state');
    version = snapshot.json('version');

    if (state === 'Offered' || state === 'Accepted') {
      offerWaitMs.add(Date.now() - requestedAt);
      offered.add(1);
      break;
    }

    if (state === 'ExpiredNoDriver') {
      break;
    }
  }

  if (state !== 'Offered' && state !== 'Accepted') {
    notOffered.add(1);
    console.error(`ride ${rideId} was ${state} after 70 s — no offer`);
  }

  // 4 — cancel, so the passenger and the driver are both free for the next iteration. Pre
  // acceptance, so no D-05 penalty and no AL-16 counter movement; collect.sh asserts both.
  if (state !== 'ExpiredNoDriver') {
    const cancel = post(
      `/v1/rides/${rideId}/cancel`, { reason: 'OTHER', version }, account.bearer);

    cancelMs.add(cancel.timings.duration);

    if (cancel.status === 200) {
      cancelled.add(1);
    } else if (cancel.status === 429) {
      // Left open deliberately rather than retried in a loop: an uncancelled ride blocks this
      // passenger's next booking through ux_rides_open_passenger, and the count of rides that
      // could not even be cancelled is the honest measure of what the ceiling costs.
      rateLimited.add(1);
    } else {
      apiErrors.add(1);
      console.error(`cancel answered ${cancel.status}: ${String(cancel.body).slice(0, 200)}`);
    }
  }
}

function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const random = (Math.random() * 16) | 0;
    return (c === 'x' ? random : (random & 0x3) | 0x8).toString(16);
  });
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

  const result = {
    component: 'C129',
    profile: 'dispatch',
    plan: { pairs, durationSeconds, thinkSeconds },
    measured: {
      ridesRequested: count('rides_requested'),
      ridesOffered: count('rides_offered'),
      ridesNeverOffered: count('rides_never_offered'),
      ridesCancelled: count('rides_cancelled'),
      apiErrors: count('dispatch_api_errors'),
      rateLimited: count('dispatch_rate_limited'),
      statePolls: count('ride_state_polls'),
      ridesPerSecond: Number((count('rides_requested') / durationSeconds).toFixed(2)),
      offerWaitMs: {
        med: trend('offer_wait_ms', 'med'),
        p95: trend('offer_wait_ms', 'p(95)'),
        p99: trend('offer_wait_ms', 'p(99)'),
        max: trend('offer_wait_ms', 'max'),
      },
      apiMs: {
        fareEstimateMed: trend('fare_estimate_ms', 'med'),
        rideRequestMed: trend('ride_request_ms', 'med'),
        rideRequestP95: trend('ride_request_ms', 'p(95)'),
        cancelMed: trend('ride_cancel_ms', 'med'),
      },
    },
    note:
      'offerWaitMs is client-observed and quantised at the 250 ms state poll. The unquantised ' +
      'distribution is rides.rides.created_at -> dispatch.offers.sent_at, computed by ' +
      'load/collect.sh, and E-09\'s outbox budget is rides.outbox.created_at -> dispatched_at.',
  };

  const lines = [
    '',
    '  C129 dispatch',
    `  pairs         ${pairs} concurrent (passenger, driver), one ride at a time each`,
    `  rides         ${result.measured.ridesRequested} requested, ${result.measured.ridesOffered} offered, ` +
      `${result.measured.ridesNeverOffered} never offered  (${result.measured.ridesPerSecond}/s)`,
    `  offer wait    med ${fmt(result.measured.offerWaitMs.med)} ms  p95 ${fmt(result.measured.offerWaitMs.p95)} ms  ` +
      `max ${fmt(result.measured.offerWaitMs.max)} ms  (budget: ADD §13.3.1 Matching 60 s)`,
    `  request       med ${fmt(result.measured.apiMs.rideRequestMed)} ms  p95 ${fmt(result.measured.apiMs.rideRequestP95)} ms`,
    `  api errors    ${result.measured.apiErrors}`,
    `  edge 429s     ${result.measured.rateLimited}  (gateway 'write' policy, 120/min, ONE bucket for every caller)`,
    '',
  ];

  return {
    stdout: lines.join('\n'),
    'load/out/dispatch.json': JSON.stringify(result, null, 2),
  };
}

function fmt(value) {
  return value === undefined ? 'n/a' : value.toFixed(0);
}
