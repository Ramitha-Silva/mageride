# Runbook — EMQX authentication failures (ADD §13.4 bullet 2)

**Alert:** `EmqxAuthFailureRateHigh` · **Severity:** page, `security: "true"`
**Dashboard:** Grafana → `mageride-emqx`

> ADD §13.4: *"EMQX auth failure rate > 1%: possible credential spray; trigger security alert."*

---

## First action

**Decide within a minute whether this is an attack or a deployment.** They look identical on the
graph and the responses are opposite.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T emqx \
  emqx ctl log set-level debug     # temporarily; revert when done
docker compose -f infra/docker-compose.dev.yml logs --tail 200 emqx | grep -i "auth"
```

- **Failures from many distinct client ids / source addresses, none of which have ever connected
  successfully** → credential spray. Escalate to security and go to "If it is an attack".
- **Failures from client ids that connected fine an hour ago** → the platform broke its own tokens.
  Go to "If it is us".

---

## What is measured

`emqx_authentication_failure / (emqx_authentication_failure + emqx_authentication_success)` over
5 minutes. The denominator is authentication *attempts*, not connections: EMQX counts a connection
that never presented a credential under `emqx_client_connect`, and dividing by that would dilute a
spray with every health check on the listener — including this stack's own blackbox TCP probe.

A device JWT is minted per vehicle (E-02) and lives at least four hours, so a healthy fleet's failure
rate is a rounding error. One percent is a lot.

---

## If it is us

The four ways the platform invalidates its own device tokens:

1. **`Mqtt__SessionTokenSecret` changed** and EMQX's `EMQX_AUTHENTICATION__1__SECRET` did not, or the
   reverse. In dev they must be the same value; the compose file passes one variable to both.
   Everything fails at once, and the rate is 100% rather than 1%.
2. **JWKS rotation** (D-21, replica and production). The broker validates against provisioning-svc's
   JWKS; a rotation with no overlap window invalidates every live session. D7' §13 gives JWT signing
   keys a 90-day rotation *with JWKS overlap* for this reason.
3. **Clock skew** on the broker host. A JWT `exp` a few minutes in the past fails, and the failures
   arrive gradually as sessions renew — which looks much more like a spray than a config error.
4. **Device certificate expiry** (T-02, 90 days). Concentrated on the trackers provisioned in one
   batch, which is a distinctive shape: one fleet, all at once, all at the same age.

```bash
# Which authenticator refused, and why.
docker compose -f infra/docker-compose.dev.yml exec -T emqx emqx ctl authn list
```

---

## If it is an attack

1. **The ACL already limits the damage.** `infra/deploy/emqx/acl.conf` (D6' §3.1) scopes every device
   to its own `veh/{vehicleId}/#` topics, and `authorization.deny_action = disconnect`. A stolen or
   guessed credential cannot read another vehicle's positions — which is why the *authorization* deny
   counter on the dashboard matters as much as this one.
2. **Rate-limit at the edge.** D-17's per-connection limit is in `emqx.conf`; HAProxy fronts 8883 as
   TCP passthrough and can drop a source address.
3. **Do not rotate the shared secret in a panic** — that fails every legitimate device at once,
   which turns a probing attempt into a platform outage. Rotate only if a credential is known to have
   leaked, and then with an overlap window.
4. Record it: EMQX's own log plus `audit.events` if any connection succeeded.

---

## Confirm the fix

The ratio on `mageride-emqx` should return under 1% within two scrape intervals. Watch the
`emqx_client_connected` rate at the same time — a ratio that improves because *successful* connects
collapsed is not a fix.

---

## What not to do

- **Do not turn off the JWT authenticator to "restore service".** That makes every vehicle's topic
  writable by anyone who can reach 8883, which is the whole of T-02 and D6' §3.1 undone. A broker
  that refuses connections is a degraded platform; a broker that accepts everyone is a compromised
  one.
- **Do not raise the 1% threshold.** It is ADD §13.4's number and the base rate on a healthy fleet is
  near zero, so 1% is already generous.
- **Do not leave `log set-level debug` on.** It logs credentials-adjacent material at volume.
