// =====================================================================================
// The MQTT session JWT, minted in the load generator (C129).
//
// The claim set is `MageRide.Shared.Mqtt.MqttSessionTokenIssuer.IssueForVehicle`'s, and the
// signature is the HMAC form `infra/deploy/emqx/emqx.conf` runs today
// (`algorithm = "hmac-based"`, the secret supplied as EMQX_AUTHENTICATION__1__SECRET).
//
// WHY THE TOKEN IS MINTED HERE AND NOT FETCHED FROM iam-svc
// ------------------------------------------------------------------------------------
// `POST /v1/auth/mqtt-token` is the real route and it is one request per vehicle behind an
// API bearer, so a 4,000-vehicle profile would open with 4,000 authenticated round trips
// against the very container whose capacity is being measured — a warm-up that is itself the
// load. Minting locally is exactly what mqtt-bridge-svc and tcp-adapter do for their own
// `svc-` credentials, with the same secret and the same claims, so the broker cannot tell the
// difference: `verify_claims` still has to hold and `acl.conf` still confines the publisher
// to one vehicle's topics.
//
// The consequence is stated in the report: this suite measures EMQX's HMAC validation path,
// and production validates RS256 against provisioning-svc's JWKS with D-21's 15-minute cache
// (the commented block in emqx.conf). Signature verification is per CONNECT, not per publish,
// so it prices connection setup and not throughput.
// =====================================================================================

import crypto from 'k6/crypto';
import encoding from 'k6/encoding';

function segment(value) {
  return encoding.b64encode(JSON.stringify(value), 'rawurl');
}

// One header for every token in a run — it never varies, and re-serialising it per vehicle
// would put a JSON.stringify on the setup path of every connection.
const HEADER = segment({ alg: 'HS256', typ: 'JWT' });

/**
 * A device session token for one vehicle.
 *
 * @param {string} vehicleId  the MQTT username, and the claim `verify_claims` compares to it
 * @param {string} secret     EMQX_AUTHENTICATION__1__SECRET / Mqtt:SessionTokenSecret
 * @param {object} [options]  {deviceId, ttlSeconds, issuer}
 */
export function mqttSessionToken(vehicleId, secret, options) {
  const settings = options || {};
  const now = Math.floor(Date.now() / 1000);

  // Four hours is `MqttOptions.SessionTokenMinimumTtl` — the floor the issuer applies when
  // there is no ride to outlive. A shorter one would have EMQX disconnect mid-run
  // (`disconnect_after_expire = true`) and the run would read as a reconnect storm.
  const ttl = settings.ttlSeconds || 4 * 3600;

  const payload = segment({
    vehicleId,
    deviceId: settings.deviceId || `load-${vehicleId.slice(0, 8)}`,
    sub: vehicleId,
    jti: `${vehicleId}-${now}`,
    iss: settings.issuer || 'mageride-provisioning',
    iat: now,
    nbf: now,
    exp: now + ttl,
  });

  const signingInput = `${HEADER}.${payload}`;
  const signature = crypto.hmac('sha256', secret, signingInput, 'base64rawurl');

  return `${signingInput}.${signature}`;
}
