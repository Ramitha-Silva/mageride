// =====================================================================================
// Where the load suite points, and what it authenticates with (C129).
//
// `load/env.json` is written by `load/configure.sh` out of `infra/replica/.env.replica` and is
// GITIGNORED, because it carries MQTT_JWT_SECRET and a set of live bearers. Nothing in `load/`
// may hold a credential in a committed file — that is the same rule `infra/CLAUDE.md` states
// for `.env*`, and it is why the profile scripts read a file rather than embedding a default.
//
// Every value can be overridden with `-e NAME=value`, so a run against a different target does
// not need the file rewritten.
// =====================================================================================

// k6 resolves `open()` against the SCRIPT's directory, not the working directory, so this
// works whether the suite is run as `k6 run load/ingest.js` from the repository root (the
// manifest's verify command) or from inside `load/`.
let file = {};
let fileError = null;

try {
  file = JSON.parse(open('../env.json'));
} catch (error) {
  fileError = error;
}

function value(name, fallback) {
  if (__ENV[name] !== undefined && __ENV[name] !== '') {
    return __ENV[name];
  }
  return fallback;
}

function number(name, fallback) {
  const raw = value(name, undefined);
  return raw === undefined ? fallback : Number(raw);
}

export const config = {
  /** wss://…:8084/mqtt — HAProxy's TCP passthrough to EMQX's `listeners.wss.default`. */
  mqttUrl: value('LOAD_MQTT_URL', file.mqttUrl),

  /** EMQX_AUTHENTICATION__1__SECRET. The CONNECT is refused with CONNACK 4 without it. */
  mqttSecret: value('LOAD_MQTT_SECRET', file.mqttSecret),

  /** https://127.0.0.1:443 — the edge, exactly as smoke.sh and gtfs-lib.sh drive it. */
  edge: value('LOAD_EDGE', file.edge),

  /** The vhost HAProxy routes on. The replica's certificate is for this name, not for the IP. */
  host: value('LOAD_HOST', file.host || 'replica.mageride.lk'),

  /** An API access token (D-29, 30 min) for `/hubs/live`. NOT the MQTT session JWT (E-02). */
  watcherToken: value('LOAD_WATCHER_TOKEN', file.watcherToken),

  /** Passenger accounts with bearers, for the dispatch profile. */
  passengers: file.passengers || [],

  /** Driver accounts with an APPROVED Mode C vehicle and a funded wallet. */
  drivers: file.drivers || [],

  /**
   * Vehicle index -> the res-7 H3 cell position-processor-svc put it in.
   *
   * Read back out of `veh:meta:{vehicleId}` by `load/configure.sh` after a warm-up publish,
   * rather than computed here. H3 is not implemented in this suite deliberately: a subscriber
   * that computed its own cell id would be asserting that two implementations of H3 agree,
   * and the failure mode (R-06's superseded "res-8 + ring(1)" figure) is an empty map rather
   * than an error. Taking the id the platform itself wrote removes the question.
   */
  cellsByVehicle: file.cellsByVehicle || {},

  /** Where the fleet is placed. Colombo Fort, so the fare tariffs and the city bounds apply. */
  originLat: number('LOAD_ORIGIN_LAT', 6.9271),
  originLng: number('LOAD_ORIGIN_LNG', 79.8612),
};

/**
 * Fails the run with the reason rather than with an exception two frames deep.
 *
 * Called from `setup()` in every profile: k6 reports a thrown error there once, before any VU
 * starts, instead of once per VU per iteration.
 */
export function requireConfigured(what) {
  const missing = [];

  for (const key of what) {
    if (!config[key] || (Array.isArray(config[key]) && config[key].length === 0)) {
      missing.push(key);
    }
  }

  if (missing.length === 0) {
    return;
  }

  const cause = fileError
    ? 'load/env.json could not be read'
    : `load/env.json is present but does not carry: ${missing.join(', ')}`;

  throw new Error(
    `${cause}.\n` +
    '  Run `bash load/configure.sh` first — it reads infra/replica/.env.replica, provisions the\n' +
    '  synthetic load accounts through the platform\'s own routes and writes load/env.json\n' +
    '  (gitignored). The replica has to be up: `bash infra/replica/deploy.sh`.');
}
