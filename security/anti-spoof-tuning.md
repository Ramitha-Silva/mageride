# C128 — anti-spoof, anti-cloning and anti-collusion under adversarial input

2026-08-12. The measurements behind `dotnet test tests/Security -c Release --filter Category=AntiSpoof`,
what they say about the deployed thresholds, and the three findings they opened.

Every number here is transcribed from a run rather than typed from memory. `MAGERIDE_ANTISPOOF_DUMP=1`
regenerates [`anti-spoof-corpus-run.md`](anti-spoof-corpus-run.md) and
[`anti-spoof-collusion-run.md`](anti-spoof-collusion-run.md), which are the evidence appendices this
document quotes.

## What was measured, and against what

| Control | ADD | Measured by | Against |
|---|---|---|---|
| Position plausibility | D-18, T-07 | `PlausibilityCorpusTests` | 35-track corpus, 1 051 samples |
| Threshold configurability | C128 fence 1 | `ThresholdConfigurationTests` | `infra/env/.env.app.example` |
| MQTT topic ACL | D6' §3.1, T-02, E-02 | `CrossVehiclePublishTests` | a real EMQX, all three listeners |
| Broker policy | D-17, D6' §3 | `BrokerPolicyTests` | `infra/deploy/emqx/*.conf` |
| Publish ceiling | D-17 | `PublishCeilingTests` | a real EMQX |
| IMEI cloning | T-08 | `ImeiCloneTests` | provisioning-svc on a real Postgres |
| Credential revocation | T-12 | `RevocationPropagationTests` | provisioning-svc + a real EMQX |
| Anti-collusion | E-07 | `RideFarmingTests` | 39-pair synthetic population |

**Nothing here asserts against a copy of a rule.** The corpus drives
`HotPath.PositionProcessor.PlausibilityFilter` with thresholds bound from the environment file a
container loads; the ACL matrix connects to a broker running the two `.conf` files the replica
mounts; the clone and revocation work composes provisioning-svc's own `ProvisioningApplication.Build`.
A measurement taken against a transcription measures the transcription.

---

## 1. The adversarial position corpus (D-18, T-07)

`tests/Security/AntiSpoof/Corpus/position-corpus.json`. Thirty-five labelled tracks — sixteen honest,
nineteen hostile, across seventeen families — expanded into 1 051 samples and run through the gate one
at a time, carrying the vehicle's last accepted position forward exactly as position-processor-svc
does, and expiring it at `VehicleMetaTtl` exactly as Redis does.

**Result: 828 honest samples, 0 refused (0.000 %). 158 hostile samples outside the documented gaps,
0 escaped (0.000 %).** The agreed bounds are 1 % and 0 % respectively, held in the corpus file
beside the data they judge.

| vehicle type | honest | refused | rate |
|---|---:|---:|---:|
| bus | 86 | 0 | 0.00 % |
| flex | 50 | 0 | 0.00 % |
| mini_van | 55 | 0 | 0.00 % |
| motorbike | 60 | 0 | 0.00 % |
| sedan | 280 | 0 | 0.00 % |
| three_wheeler | 188 | 0 | 0.00 % |
| truck | 8 | 0 | 0.00 % |
| van | 101 | 0 | 0.00 % |

Every hostile family is caught by the gate the corpus names it for — a teleport refused for having
too few satellites would be a pass that survives the teleport gate being deleted, so the check tag is
asserted as well as the refusal.

### The modelling decision the whole number rests on

Receiver error is modelled as a **bounded random walk**, not as an independent draw per fix
(`CorpusLeg.JitterDriftM`). GNSS error is strongly autocorrelated: multipath geometry changes over
seconds, so a receiver reading 30 m off truth reads roughly 30 m off truth a second later and in
roughly the same direction.

This is not a detail. An earlier draft drew each fix independently from a 30 m disc, which implies up
to 60 m of displacement per second — 216 km/h of pure noise — and reported a false-positive rate on
the three-wheeler tier that does not exist in the field. **The 80 km/h three-wheeler ceiling looks
unusable under the wrong error model and is comfortable under the right one.** Any future retune
should check which model a claimed false positive came from before changing a threshold.

The corpus's hardest honest case is `honest-three-wheeler-canyon-one-hertz`: the lowest ceiling ADD
§12.6 sets (80 km/h), the fastest cadence the platform ever asks for (AL-12's 1 sample/s inside 150 m
of pickup), and the worst receiver on the platform (a GT06 in the Pettah canyon, 45 m of wander
moving 14 m per second). It passes with nothing refused, and it is the track a retune should look at
first.

### The four attacks the gate does not catch, and what does

Held in the corpus as `knownGap` and asserted to **still escape**, so closing one fails the suite and
asks for the entry to be deleted. A ledger that outlives its defect is worse than none.

| Track | What escapes | The control that answers it |
|---|---|---|
| `hostile-slow-walk-to-a-fabricated-location` | A spoofer moving at a speed the tier allows | Nothing in D-18/T-07 — identity, not physics: mTLS device binding, the topic ACL, T-08 |
| `hostile-silence-then-reappearance-past-the-meta-ttl` | Going quiet for 11 min, then reappearing 200 km away | `VehicleMetaTtl` is the gate's horizon; dispatch's T-11 30 s freshness rule will not offer a ride to a vehicle dark that long |
| `hostile-teleport-inside-a-backlog` | A teleport published on `pos/replay` | T-05: the 20 samples/s pacer and the `seq` watermark. Accuracy and satellite gates still apply |
| `hostile-time-rewound-handset-track-continuing-in-place` | A handset rewinding its clock while driving plausibly | The R-17/T-05 `seq` watermark — the clock gate is hardware-only by design |

The last one is worth a number: on the mobile plane a rewound sample is judged over the
`MinStepInterval` floor, which accepts **any position within `tierCeiling × MinStepInterval` of the
last accepted fix** — 50 m for a sedan at a 1 s floor.

**A green corpus does not mean GPS spoofing is solved.** It means every attack somebody wrote down is
either refused or has a named owner elsewhere.

### One result worth pinning

Replaying a recorded track live from a **handset** is caught, despite the monotonic-clock gate being
hardware-only — because a replay rewinds *position* as well as time, so its opening sample lands back
at the start of the recorded track while the vehicle's last accepted fix is at the end of it. 18 of
20 samples are refused; the tail is refused only up to the point where the replay coincides with
where the vehicle actually is, and a replay that has caught up with reality is a parked vehicle.

### Tuning changes made

**None to the thresholds.** ADD §12.6's table, D-18's 200 m and the 1 km/s jump backstop all measure
clean against a corpus built to stress them, and changing a number that is not wrong would have been
churn. Two configuration changes were made:

- **`PositionProcessor__VehicleMetaTtl` added to `infra/env/.env.app.example`.** It was reachable
  only as a C# initialiser, and it is not an ordinary TTL — it is the step gate's horizon, and
  therefore the setting that decides how long a spoofer must stay quiet to relocate for free.
  Shortening it narrows that window; an operator could not previously do so without a build.
- The E-07 block, below.

### One spec observation

ADD §12.6 prices `flex` at **200 km/h — the highest ceiling in the table, above sedan's 180** — for
what D5' §1's enumeration lists as a passenger tier between `three_wheeler` and `sedan`. Nothing on a
Sri Lankan road legally approaches it (the expressway limit is 100 km/h), so the tier's per-type
ceiling is effectively no ceiling, and `DefaultMaxSpeedKph` inherits the same value for the three
registry types the table omits (`truck`, `mini_truck`, `train`). The jump backstop still applies.
Recorded as a micro-change-set rather than changed here: this is a spec question, and the corpus
measures what is deployed.

---

## 2. MQTT ACL and the D-17 ceiling

### Every listener, not the one that was tested

The deployment has three live listeners and they share the ACL file and nothing else — the transport
differs, and so does the mechanism that decides the principal:

| Listener | Transport | Principal from | Who uses it |
|---|---|---|---|
| `1883` tcp | plain TCP | verified `vehicleId` JWT claim | mqtt-bridge, tcp-adapter — **in-cluster only** |
| `8084` wss | MQTT over WSS | verified `vehicleId` JWT claim | **a driver's handset** |
| `8883` ssl | mutual TLS | certificate CN (`peer_cert_as_username`) | hardware trackers (T-02) |

**8084 had never been driven by any suite before C128.** `EmqxAuthTests` (C024) covers 1883 and
`EmqxDeviceCertificateTests` (C030) covers 8883; the listener with no coverage was the one a phone
actually connects to, and 1883 — the one with the most — is documented as never published past the
docker network. `MageRide.TestKit.EmqxFixture` now publishes 8084 as well, and the matrix runs six
assertions on each of the three planes.

**Result: a cross-vehicle publish is refused on all three.** So is a publish outside the vehicle
tree, a subscribe to another vehicle's command topic, and any attempt to hold the `$share/posGroup`
subscription. On the two JWT planes a token minted for another vehicle is refused at CONNECT; on the
tracker plane the equivalent is that a device holding vehicle A's certificate is confined to vehicle
A's topics whatever client id it presents, because the CN is not a field a client chooses.

Each plane also asserts the two things a broker that refused everything would fail: a device can
publish its own position and subscribe to its own downlink.

### The file, not just the connection

`BrokerPolicyTests` reads `infra/deploy/emqx/{emqx.conf,acl.conf}` with comment lines stripped, so a
setting that exists only inside a commented-out block cannot satisfy an assertion. It pins:
`messages_rate = 5/s` and `max_conn_rate = 500/s` on **every** live listener; `no_match = deny` and
`deny_action = disconnect`; every device grant scoped to `${username}`; the wildcard grants
restricted to the `^svc-` prefix; `listeners.ws.default` (plaintext WebSocket, 8083) disabled; and
`enable_authn = false` present on exactly the listener that also demands `verify_peer` +
`fail_if_no_peer_cert`.

That last pairing is the one worth stating: `enable_authn = false` is correct on 8883 because a
hardware tracker has no session token to present as a password — but it is only correct while the
handshake is genuinely mutual. If `verify_peer` were ever relaxed, that single line would turn the
tracker plane into an unauthenticated listener and nothing else in the file would object.

### The 5 msg/s ceiling, measured

**EMQX paces rather than drops.** Forty publishes on one connection take longer than the ceiling
allows and none is refused, on both JWT planes. A test that looked for a refusal would have found
none and reported the ceiling as absent.

**Four sessions under one credential beat it**, which is not a failure — a listener limiter sees a
socket, not a principal, and nothing in D6' §3 asks EMQX to enforce a per-vehicle rate. It is the
entire reason the other two lines exist, and it is invisible from either of them alone:

| Line | Where | Scope | On breach |
|---|---|---|---|
| 5 msg/s | EMQX `messages_rate` | per **connection** | publisher paced |
| 5 msg/s | mqtt-bridge-svc | per **vehicle** | `mqtt.rate_violation`; nothing dropped |
| 10 msg/s / 10 s | position-processor-svc | per **vehicle** | **dropped** + flagged |

The ordering is asserted: the dropping line must sit above the reporting one, or a vehicle publishing
at exactly the cadence the platform asked it for would lose samples at the backstop.

---

## 3. IMEI cloning (T-08) and revocation (T-12)

### Cloning

**A cloned IMEI holds both devices, and stops serving either, in well under the 60 s budget.** Both
`prov.tracker_bindings` rows go `QUARANTINED` with reason `imei-duplicate`, the incumbent's
credential stops validating even though it was legitimately issued, and the adapter's `imei:{imei}`
fast path is deleted — left behind, a reconnecting device would be resolved from it for the cache's
whole 24 h TTL without ever asking `validate`, which is how a 60 s budget quietly becomes a day.

The **window** is asserted from both sides at the deployed 24 h: 23 hours apart is a clone, 25 hours
apart is a re-provision that supersedes cleanly. The fixture ages the *sighting trail* rather than
shortening `Provisioning:AntiCloneWindow`, because the DoD's claim is about the documented window and
a test that redefined it to two seconds would prove the mechanism while saying nothing about the
number.

The second detection path is covered too: a clone that copies the credential never reaches `bind` at
all, so what distinguishes it is two live sockets holding one identity — state the adapter has and
provisioning-svc does not. The adapter reports, this service adjudicates, and a repeated report is a
no-op because the adapter re-validates every five minutes and will report again.

### Revocation — the TCP path meets the budget

A decommission stops the credential validating immediately, and the `prov:tracker` signal the adapter
force-closes on reaches Redis inside ADD §7.7.3's one-second budget carrying the type, the IMEI, the
vehicle and the serials it invalidates. The field names are asserted because tcp-adapter deserialises
into its own copy of the record: a rename on either side turns every field null and the socket simply
never closes, with nothing logged at either end.

A **rotation is not a revocation** — the outgoing credential keeps validating and neither serial
reaches the CRL. Conflating them would take every device offline on its rotation day.

### Revocation — the MQTT path does not exist · **C128-01**

**A revoked tracker certificate still completes the mutual-TLS handshake and still publishes
positions.** Measured, not inferred: mint a certificate from the CA the broker trusts, connect,
revoke it, connect again. The second handshake succeeds.

Everything on the platform side works. The binding goes `REVOKED`, `validate` answers no, and the
serial is on the CRL provisioning-svc publishes, inside the budget. What is missing is the broker
reading it: `enable_crl_check` and `crl_cache.refresh_interval` are **commented out** in
`infra/deploy/emqx/emqx.conf`, which is the file the dev stack, the replica and the test fixture all
mount.

**It cannot simply be switched on.** EMQX locates a CRL through the *CRL distribution point extension
in the peer certificate*, and `EmbeddedStepCa` writes that extension only when
`StepCa:CrlDistributionPoint` is configured — which no environment sets. Every certificate the
platform has ever minted therefore carries no distribution point, and a broker with
`enable_crl_check = true` refuses a certificate whose CRL it cannot fetch. Turning the check on
before re-minting the fleet does not tighten the tracker plane; it takes the whole of it off the air.

Full write-up, ordering and owner in [`remediation-backlog.md`](remediation-backlog.md) (C128-01).
Both the measurement and `BrokerPolicyTests` assert the *current* state, so the day the control is
turned on the suite fails and sends the reader to the entry to delete it.

---

## 4. Anti-collusion (E-07)

Measured against a 39-pair synthetic population shaped like a Sri Lankan ride-hailing month: 33
honest pairs from one-off riders through to a weekday return commuter at 34 rides with one
counterparty, and 6 farming pairs at 12–27 rides who also share a handset.

```
repeat_pair at threshold 8/30d: 9 flags over 33 honest and 6 farming pairs — precision 67 %.
  honest 'weekday-one-way' pairs flagged: 2 (at 14 rides each)
  honest 'weekday-return-commuter' pairs flagged: 1 (at 34 rides each)
  correlated with the device cross-check: 6 flags, precision 100 %.
```

**Recall is 100 % and is asserted**; a farming pair the detector misses is the control failing.
Precision is measured and reported rather than bounded, because it depends on a population nobody can
know in advance and a bound would be a guess wearing an assertion's clothes.

### The finding: pair frequency alone is not the control

**A farming pair rides *less* than a commuter.** In Sri Lanka a passenger keeping one three-wheeler
driver on call is ordinary, and twice a day on weekdays is 34 rides with one counterparty — over any
threshold that would still catch farming. So **no value of `PairRideThreshold` separates them**, and
raising it is not the fix: it drops the commuters and the farmers together.

What does separate them is the cross-check ADD §12.6 already names. Correlating `repeat_pair` with
`shared_device` named **exactly the six farming pairs and nothing else** on the same population.

The detector already computes all three signals. What it does not do is correlate them: it raises
`repeat_pair`, `shared_device` and `network_cluster` as three independent flags, so an admin sees
three queues rather than one ranked one. That is a surfacing question rather than a detection one —
the information is all present in `reputation.fraud_flags` — and it is recorded as **C128-02** with
admin-bff as the owner rather than changed here, because the fence for this component is that
anti-collusion output is a review signal and the review surface is not C128's to redesign.

`PairRideThreshold` is therefore **left at 8**, deliberately, with the correlation recorded as the
control that makes it usable.

### The fence

Every test that raises a flag also asserts `reputation.block_states` did not move — including the
worst case, where all three detectors fire on one cluster of four accounts. ADD §12.6's own row reads
"auto-suspends both accounts on Tier-2 thresholds"; C033 resolved that by giving the auto-suspend to
an admin decision, and this is the assertion that keeps it there. It matters more given the precision
above: a detector that could block would make every one of those three commuter flags an account
suspension rather than a queue item.

### Tuning change made

**The six E-07 thresholds now have lines in `infra/env/.env.app.example`.** They had none and were
reachable only as C# initialisers. For a detector whose entire output is a human review queue, that
meant its volume — the only thing about it an operator ever needs to change — could not be changed
without a build.

---

## Findings opened

| # | Finding | Severity | State | Owner |
|---|---|---|---|---|
| C128-01 | A revoked tracker certificate still authenticates to EMQX: no deployed broker checks the CRL | **HIGH** (prod) / LOW (replica) | open; needs a fleet re-mint first | C133, before go-live |
| C128-02 | E-07 raises three uncorrelated flags; the correlation is what has the precision | MEDIUM | open | admin-bff (C061) |
| C128-03 | `flex` is priced above `sedan` in ADD §12.6's anti-spoof table | LOW | micro-change-set | spec owner |

Fixed in this component: `PositionProcessor__VehicleMetaTtl` and the six `Reputation__Collusion__*`
keys added to `infra/env/.env.app.example`, both under C128 fence 1.

## Running it

```bash
dotnet test tests/Security -c Release --filter Category=AntiSpoof     # the whole thing
MAGERIDE_ANTISPOOF_DUMP=1 dotnet test tests/Security -c Release --filter Category=AntiSpoof
```

The second form rewrites the two evidence appendices. The corpus, threshold and broker-policy
assertions need nothing but a build agent; the ACL matrix, the clone work, the revocation
measurements and the collusion population need Docker and **skip loudly** without it — see
[`../tests/Security/CLAUDE.md`](../tests/Security/CLAUDE.md) for why that exception is scoped to this
category.
