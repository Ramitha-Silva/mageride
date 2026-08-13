// =====================================================================================
// Where the chaos client-side drills point (C130).
//
// A near-copy of load/lib/config.js and deliberately its own file: k6 resolves `open()` against
// the directory of the module that CALLS it, so importing C129's config would read
// `load/env.json` — the capacity suite's fixture, with the capacity suite's twelve accounts and
// its 750-vehicle cell map. The two suites keep separate credentials on purpose (see
// chaos/configure.sh's header) and a shared config would quietly undo that.
//
// Everything else in `lib/` IS C129's, imported across the two directories rather than copied:
// `MqttClient` (MQTT 3.1.1 over WSS), `mqttSessionToken` and the CBOR position codec are the
// platform's k6-side wire implementations and a second copy would drift from the deployed one.
// The `will` option and `abort()` those drills need were added to C129's client rather than
// forked — additive, and recorded in the C130 handoff.
// =====================================================================================

let file = {};
let fileError = null;

try {
  file = JSON.parse(open('../../env.json'));
} catch (error) {
  fileError = error;
}

function value(name, fallback) {
  if (__ENV[name] !== undefined && __ENV[name] !== '') {
    return __ENV[name];
  }
  return fallback;
}

export const config = {
  /** wss://…:8084/mqtt — HAProxy's L4 passthrough to EMQX's `listeners.wss.default`. */
  mqttUrl: value('CHAOS_MQTT_URL', file.mqttUrl),

  /** EMQX_AUTHENTICATION__1__SECRET. Without it every CONNECT is answered CONNACK 4. */
  mqttSecret: value('CHAOS_MQTT_SECRET', file.mqttSecret),

  edge: value('CHAOS_EDGE', file.edge),
  host: value('CHAOS_HOST', file.host || 'replica.mageride.lk'),

  passengers: file.passengers || [],
  drivers: file.drivers || [],
};

export function requireConfigured(keys) {
  const missing = keys.filter((key) => {
    const held = config[key];
    return held === undefined || held === null || held === '' ||
      (Array.isArray(held) && held.length === 0);
  });

  if (missing.length > 0) {
    throw new Error(
      `chaos/env.json is missing ${missing.join(', ')}` +
      (fileError ? ` (it could not be read: ${fileError.message})` : '') +
      '. Run `bash chaos/configure.sh`.');
  }
}

/**
 * The synthetic vehicle ids the storm and flood drills use.
 *
 * `c0a0c0a0-…`, a pure function of the index, for load/lib/fleet.js's reason: a k6 VU, a shell
 * script and a psql query name the same vehicle without sharing a file, and everything a run
 * leaves behind is greppable. Distinct from C129's `10ad10ad-…` block so a chaos run and a
 * capacity run can never be reading each other's rows.
 *
 * None of them is in `registry.vehicles` and none needs to be: `telemetry.positions` has no FK to
 * it (migration 1801 says why), position-processor-svc keys on the vehicleId EMQX authenticated,
 * and `acl.conf` grants a device its own `veh/{vehicleId}/*` regardless.
 */
export function chaosVehicleId(index) {
  return `c0a0c0a0-0000-4000-8000-${String(index).padStart(12, '0')}`;
}
