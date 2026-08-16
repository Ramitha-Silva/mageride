# Runbook — the day-0 GTFS load, and every refresh after it

**C126.** How the national GTFS feed gets into MageRide: obtain it, upload it through
**SCR-AP-016**, read the validation report, preview it, activate it, verify that passengers are
served by it, and roll back if it turns out to be wrong.

This is the launch gate for Mode A route matching (US-8.2b). Until a feed is active,
`GET /v1/transit/options` answers `coverage: no_feed` and SCR-PA-009 hides route matching entirely —
which is a **safety net, not the launch state** (AL-55).

| | |
|---|---|
| **Scripts** | `infra/replica/gtfs-day0-load.sh` (the operation) · `infra/replica/gtfs-day0-verify.sh` (the check) |
| **Screen** | SCR-AP-016 GTFS Dataset Manager — Admin Portal → Configuration → Transit data |
| **API** | `POST /v1/admin/transit/gtfs/uploads` → `GET …/uploads/{id}` → `POST …/uploads/{id}/activate` → `GET …/versions` |
| **Who** | Admin or Super Admin only (AL-06). Verification, Support, Finance and Auditor roles have no Transit-data nav entry and no route |
| **Audit** | Every upload, verdict and activation writes `audit.events` with `entity_type = 'gtfs_feed'` (D-35) |

---

## 0. The one rule

**The feed is an externally provided file — the day-0 national release and every refresh alike
(AL-56).** There is no in-house sourcing, authoring, surveying or corridor-gating workstream; the
standalone GTFS acquisition plan was retired on 2026-07-23. MageRide's only quality gate is
server-side validation (BR-32.1).

So: nothing in this runbook, and nothing in either script, edits, repairs, filters, trims or
generates feed content. A feed that fails validation is fixed **at the provider** and re-uploaded.
If you find yourself opening `stop_times.txt` in a text editor, stop — that file is somebody else's
statement about the country's bus network, and a local edit makes the next refresh silently
disagree with it.

---

## 1. Obtain the feed

Get two files from the provider:

| File | Why both |
|---|---|
| the **current** national release | this is what goes live |
| the **previous** national release | the rollback rehearsal needs a version to roll back *to*, and BR-32.1 dedupes uploads on sha256 — the same file cannot become a second version, so a feed cannot be rolled back to itself |

Check before you go further:

- it is a **zip**, ≤ 200 MB (BR-32.1, `Transit__Gtfs__MaxUploadBytes`);
- it contains `agency.txt`, `routes.txt`, `trips.txt`, `stops.txt`, `stop_times.txt` and at least one
  of `calendar.txt` / `calendar_dates.txt` — the validator will tell you if not, and a **missing
  required file stops the validation pass** rather than producing half a million consequential
  errors;
- you know **which release it is**. `feed_info.txt`'s `feed_version` becomes the version string on
  every screen and in every log line. A feed without one is legal and shows as `(none in the file)`,
  which makes two releases indistinguishable in the history table.

Then drop them where the scripts look:

```bash
mkdir -p infra/replica/gtfs
cp <current-release>.zip   infra/replica/gtfs/national.zip
cp <previous-release>.zip  infra/replica/gtfs/national-previous.zip     # the -previous suffix is the convention
```

`infra/replica/gtfs/` is gitignored. **Never commit a feed** — it is a third-party dataset, it is
tens of megabytes, and the repository is not its distribution channel.

---

## 2. Record the empty state FIRST

The pre-first-import state is a definition-of-done item (D2 SCR-AP-016 "Empty state") and it stops
existing the moment anything is activated. If the file has not arrived yet, record the baseline
now — it costs nothing and cannot be reconstructed later:

```bash
bash infra/replica/gtfs-day0-load.sh --observe-empty-state
```

That provisions the day-0 operator, proves the admin surface answers, and writes
`infra/replica/gtfs-day0-journal.json` with the three facts that matter: the version history is
empty, `transit.gtfs_*` is empty, and `GET /v1/transit/options` answers **`coverage: no_feed`**.

`coverage` is why this is worth capturing. After day-0, an empty options list means "no bus serves
this corridor". Only the discriminator separates that from "we cannot tell", and this is the one
chance to see the second value on a real deployment.

**Recorded on this replica, 2026-08-11T14:35:49Z:**

| | |
|---|---|
| `GET /v1/transit/options` (Pettah → Kottawa) | `coverage: no_feed`, 0 options |
| `transit.gtfs_routes` / `_trips` / `_stops` / `_stop_times` / `_shapes` | 0 / 0 / 0 / 0 / 0 |
| `transit.gtfs_feed_versions` | 0 rows — SCR-AP-016 renders its empty state |
| app-services log, at start-up | `transit-svc routing is up: halt radius 400 m … feed cache on` |

---

## 3. Load it

```bash
bash infra/replica/gtfs-day0-load.sh --feed infra/replica/gtfs/national.zip \
                                     --previous infra/replica/gtfs/national-previous.zip
```

With both files in `infra/replica/gtfs/` under the naming convention, the flags can be omitted.

The script walks SCR-AP-016's own sequence, through the edge, with a real Admin bearer:

| Step | What happens | What to look at |
|---|---|---|
| 3 | the **previous** release is uploaded, validated and activated | it has to have been live to be `archived`, which is what makes it a rollback target |
| 4 | the day-0 feed is uploaded (`202`, a `feedVersionId`) and validation is polled every 2 s | `Uploaded → Validating → Validated` — the same stepper the screen shows |
| 5 | the **preview** is printed: per-file counts, `feed_info` version, service window, warnings | this is the review. See §4 |
| 6 | **activate** — one transaction, then the ≤ 60 s cache reload | the swap is a catalogue rename, so it takes about as long on a national feed as on a toy one |
| 7 | the corridor sample set is asked | §5 |
| 8 | a refused activation is shown to leave the live feed alone | a version id that cannot exist, so nothing real is at risk |
| 9 | **rollback rehearsal**, then restore | §6 |
| 10 | the history table, as SCR-AP-016 lists it | exactly one `ACTIVE` row |

Validation of a national feed is minutes, not seconds — it streams every row of `stop_times.txt`
and checks referential integrity against `stops.txt`, `trips.txt` and the calendar. The default
ceiling is 30 minutes (`GTFS_VALIDATION_TIMEOUT`).

Re-running is safe. The upload answers `409 feed-duplicate` naming the version the first attempt
created and the script carries on with it; an already-active feed answers `409 feed-already-active`
and nothing is swapped; the recorded empty state is never overwritten.

---

## 4. Read the validation report before activating

**Errors block, warnings do not** (BR-32.1). That line is the line between "this dataset would
break route matching" and "somebody should look at this".

| | |
|---|---|
| **Errors** → status `failed`, activation impossible | a missing required file, an empty file, a `stop_times` row naming a stop that does not exist, a stop outside Sri Lanka (5.7–10.0 °N, 79.4–82.1 °E), a malformed time |
| **Warnings** → activation allowed | a service window ending in under 30 days, stable ids that disappeared since the active feed, an optional file absent |

The response caps the error summary at five. The full row-level report — one row per finding,
severity first — is:

```bash
curl -sk -H "Host: $REPLICA_HOSTNAME" -H "Authorization: Bearer $TOKEN" \
  "https://127.0.0.1/v1/admin/transit/gtfs/uploads/<feedVersionId>/report?format=csv" -o report.csv
```

On the screen that is **Download error report**. `ErrorCount` is uncapped and is what decides
`failed`, so "5 errors shown" can mean half a million: a feed whose `stop_times.txt` names a
nonexistent stop is wrong on every row.

**A failed feed is fixed at the provider (AL-56).** Send them the CSV.

---

## 5. Verify that passengers are served

```bash
bash infra/replica/gtfs-day0-verify.sh
```

Read-only, re-runnable, and it re-derives everything that is still observable rather than trusting
the journal: the active version, the row counts, the corridor answers, the shapes, the
one-active-row invariant, the audit trail. Only the three unobservable facts — the empty state and
the two swap timings — come from the journal.

The corridor sample set is `infra/replica/gtfs-corridors.json`: six real corridors chosen to fail
for different reasons — the two Colombo trunks, one that leaves the district, one in Kandy (so a
Colombo-only feed is caught), one in the deep south (so the bounding box is exercised near its
floor), and the 115 km Colombo–Kandy backbone, which is also a rail corridor.

Per corridor, the **hard** checks are: `coverage: active`; at least one **DIRECT** option; and every
direct leg's shape decodes, lies inside the same bounding box the validator applies to stops, and
passes within 1200 m of both ends of the corridor. That last one is what distinguishes a correct
shape from a valid, well-formed, *wrong* one.

`expectRoutes` — "138 should be on Pettah → Kottawa" — is a **soft** check, reported loudly and
never fatal. Route numbering is the feed's to state, not ours to require (AL-56); a verify that
failed because the provider renamed 138 to 138/1 would be asserting against the wrong thing. But if
a corridor returns routes and not the one everybody in Colombo rides, ask the provider about it.

---

## 6. Rollback

**A rollback is an activation.** Same endpoint, same transaction, same ≤ 60 s cache reload; on the
screen it is the history row's **Re-activate**. The rehearsal in step 9 does it twice — back to the
previous release, then forward to the day-0 feed, so the replica is left in its day-0 state.

```bash
# the same call the screen makes
curl -sk -X POST -H "Host: $REPLICA_HOSTNAME" -H "Authorization: Bearer $TOKEN" \
  -H "Idempotency-Key: $(openssl rand -hex 16)" -H 'Content-Length: 0' \
  "https://127.0.0.1/v1/admin/transit/gtfs/uploads/<archivedFeedVersionId>/activate"
```

### Timings

Filled in from `infra/replica/gtfs-day0-journal.json` by the run itself. **The national feed has not
been loaded yet** (see §9), so the real column is still open:

| | Swap (HTTP) | Cache reload | Bound |
|---|---|---|---|
| day-0 activation | *(pending)* | *(pending)* | ≤ 60 s (US-28.2) |
| rollback to the previous release | *(pending)* | *(pending)* | ≤ 60 s |
| restore of the day-0 feed | *(pending)* | *(pending)* | ≤ 60 s |

**Measured on a rehearsal, 2026-08-11 — a SYNTHETIC fixture, not a feed.** Two generated
Gampaha-district datasets (15 halts, 3 vs 4 routes, 288 vs 408 `stop_times`) were driven through the
whole pipeline to exercise the paths a feed-less run cannot reach. Read these as mechanics, not as a
forecast:

| | Swap (HTTP) | Cache reload |
|---|---|---|
| activate v1 over an empty database | 1.3 s | — |
| activate v2 over v1 (the "day-0" swap) | **0.2 s** | **4.3 s** |
| roll back to v1 | **0.3 s** | **4.3 s** |
| restore v2 | 0.2 s | 4.3 s |

**What that does and does not predict.** The swap is `ALTER TABLE … SET SCHEMA` — a catalogue update
that rewrites no row — so 0.2 s is roughly what a national feed will cost too. The two phases that
*do* grow are the **staging load** before it (minutes for a national feed; nothing is visible while
it runs) and the **cache rebuild** after it: `Loaded GTFS feed … in 00:00:00.018` for 15 halts
becomes seconds-to-tens-of-seconds for ~7 600, and the 4.3 s above is dominated by the poll interval
rather than the work. That is exactly what the ≤ 60 s bound is for.

**What the rehearsal proved** (34 of 35 verify checks green — the miss is the empty state, which
belongs to the real run's journal): the staging load, the three-way schema rename, `NOTIFY` inside
the swap transaction, the cache reload, corridor matching with correct shapes, rollback in both
directions, the one-active-row index, audit attribution, and stored-zip retention across the volume.

**The strongest single check was a discriminator, not a timing.** Route 224 exists in v2 and not in
v1, so `Gampaha → Mirigama` must answer differently either side of a swap. It did:

| | direct options | `transit.gtfs_routes` |
|---|---|---|
| v2 live | `224` | 4 |
| after rollback to v1 | *(none)* — and `coverage: active`, not `no_feed` | 3 |
| after restoring v2 | `224` | 4 |

That is the swap replacing the *dataset* rather than flipping a ledger row — and the middle row is
AL-55's distinction on a real deployment: "no bus serves this corridor" is `active` with an empty
list, which is not the same answer as "there is no feed".

What to expect and why: the swap is `ALTER TABLE … SET SCHEMA`, a catalogue update that rewrites no
row, so it does not grow with the feed. The **staging load** before it does — that is where the
minutes go, and it happens before anything is visible. The cache reload is triggered by `NOTIFY`
issued *inside* the swap transaction, so it starts when the swap commits; the 30 s safety-net poll
is what makes 60 s a guarantee rather than a hope.

### Two things that will bite

- **A rollback re-imports from the archived version's stored zip.** No feed zip is ever deleted
  (BR-32.3's ≥ 12-month retention is met by the absence of a delete path), but they live under
  `Transit__Gtfs__StorageRoot` — and until C126 that path was on the container's writable layer, so
  the next `deploy.sh` silently deleted every rollback target. It is now the `gtfsdata` volume, and
  `gtfs-day0-verify.sh` §6 checks both the mount and the file count.
- **A rollback must clear `archived_at`.** `ck_gtfs_feed_versions_activated` refuses an `active` row
  that still carries one, which is exactly the row a naive rollback writes. The service does this;
  `migrate-verify.sh` asserts the constraint rejects the alternative.

---

## 7. The checklist for every refresh

Same pipeline, no authoring. A refresh is not a different operation from day-0 — it is day-0 with a
non-empty history table.

- [ ] The file came **from the provider**. Nothing local edited it.
- [ ] `feed_info.txt`'s `feed_version` differs from the live one, so the history table can be read.
- [ ] The **currently active** version id is written down before you start. It is the rollback target.
- [ ] Uploaded through SCR-AP-016. Not through psql, not through a job, not through
      `POST /v1/admin/transit/gtfs-import` — which is superseded and, as `gtfs-day0-verify.sh` §7
      asserts, not mapped.
- [ ] Validation reached **Validated**. Errors are zero; every warning has been read and accepted.
- [ ] The preview's counts are within reach of the live ones. A national feed that lost 40 % of its
      trips overnight is a provider incident, not a release.
- [ ] The service window covers the period from now — BR-32.1 warns under 30 days and
      `gtfs-day0-verify.sh` §2 fails outright on a window that has **ended**.
- [ ] Activated, and the cache reload was inside 60 s.
- [ ] `bash infra/replica/gtfs-day0-verify.sh` is green, including the corridor sample.
- [ ] The previous version shows **Archived** in the history table and its zip is still stored.
- [ ] If any of the above is wrong: **re-activate the previous version** (§6). That is the whole
      point of keeping it.

---

## 8. When something is wrong

| Symptom | Cause | Fix |
|---|---|---|
| upload → `409 feed-duplicate` | this exact file is already a version (sha256, BR-32.1) | the response names the version — go and look at it. Not an error on a re-run |
| status stuck at `validating` | the validation worker is not running, or `Transit__Gtfs__ValidationEnabled=false` — off means nothing can ever be activated | check the app-services log for `GtfsValidationWorker`; the 15 min stale latch re-queues a validation whose replica died |
| activate → `409 feed-not-validated` | the version is `uploaded` or `failed` | only a `validated` (or `archived`) version can go live |
| activate → `409 conflict` | another operator holds the activation advisory lock | one activation at a time, across both phases, by design. Wait |
| activate → 5xx | the swap rolled back | **the previous feed is still live and untouched** — the staging load only ever writes `transit_staging.*`. Read the log, fix the cause, activate again |
| `coverage: no_feed` after activating | the cache did not reload | `LISTEN transit_feed_activated` plus a 30 s poll; look for `Reloading the GTFS feed failed` — a failed reload leaves the *previous* feed published, which is deliberate |
| a corridor returns no direct route | the feed does not serve it, or its halts are further than `Transit__HaltRadiusM` (400 m) from the sample point | check `transit.gtfs_stops` near the coordinates before blaming the router |
| every corridor returns no direct route | `Transit__FeedCacheEnabled=false` — off means every corridor answers `no_feed` | it is announced at start-up. Read the first transit-svc log line |
| a 500 on any authenticated request | `Jwt__JwksUrl` unreachable | fixed in C126 — the value must be `http://app-services:5000/v1/.well-known/jwks.json` and the gateway must carry the `iam-jwks` route. `curl` it from inside the container |
| download link → `401` | the signed URL expired (15 min) or `Transit__Gtfs__DownloadSigningKey` changed | ask for a new 302 from `…/versions/{id}/download` |

---

## 9. State of this replica

| | |
|---|---|
| **Pre-first-import empty state** | recorded, 2026-08-11 (§2) |
| **Day-0 feed** | **not loaded.** No national GTFS file exists in this repository or on this box, and AL-56 forbids manufacturing one. The load, the corridor verification and the rollback rehearsal all run the moment the provider's zip is dropped at `infra/replica/gtfs/` — or the moment it is uploaded through SCR-AP-016 in the Admin Portal, which is behind `--profile portals` and is not running by default |
| **Wave-5 gate** | the "day-0 GTFS feed active" clause was **excepted on 2026-08-12** — the feed is an external dependency, so wave 6 (C127–C132) is not blocked on it. **C133's go-live gate is not excepted and still requires an active feed**, which is the point of AL-55: no-coverage is a safety net, not a launch condition |
| **Pipeline rehearsal** | **done, on a synthetic fixture** (2026-08-11): activate → cache reload → roll back → restore, plus the corridor and shape checks. See the rehearsal tables in §6. `/root/gtfs-fixtures/` holds the two zips and the generator that made them; they are deliberately NOT in `infra/replica/gtfs/`, where `gtfs-day0-load.sh` would pick one up as the day-0 feed |
| **Operator** | `gtfs-day0@replica.invalid`, role `admin`, password generated into `.env.replica` (gitignored). Synthetic, replica-only |
| **Retention** | `gtfsdata` volume mounted at `/var/lib/mageride` in app-services, proven to survive a container recreate |
| **Download signing key** | generated per deployment by `deploy.sh`; the repository's `CHANGEME_…` placeholder is no longer live here |

**The history table currently holds three rows, none of them a feed.** A synthetic Gampaha fixture is
**ACTIVE** (`SYNTHETIC-gampaha-v2-2026-08-11`), its v1 is **ARCHIVED**, and a malformed probe is
**FAILED**. Uploading the provider's file archives the synthetic one automatically — the pipeline
needs no cleaning up first — but if you would rather start from a bare history, the rows and their
stored zips go together:

```sql
-- the fixtures and the probe; audit.events keeps its record of them either way (append-only)
DELETE FROM transit.gtfs_feed_versions
 WHERE file_name IN ('gampaha-SYNTHETIC-v1.zip','gampaha-SYNTHETIC-v2.zip','malformed-not-a-feed.zip');
```

Do that **only while no fixture is active** — deleting the active row leaves `transit.gtfs_*` holding
its dataset with nothing in the ledger pointing at it, which reads as a feed nobody activated. Roll
forward to the real feed first, or accept the synthetic row being archived beneath it.

**One `FAILED` row is a malformed-input probe, not a feed.** The upload path had never
executed against a deployment, so it was exercised with a zip containing a single README saying what
it is — no GTFS file, so BR-32.1 refused it with five `missing_file` errors and it can never be
activated. What that proved, end to end through HAProxy and the gateway: the multipart upload
(`202` + `feedVersionId`), the `Idempotency-Key` requirement, the validation stepper reaching
`failed`, the capped five-error summary, the CSV row-level report, the sha256 duplicate refusal
naming the existing version, and both audit rows (`GTFS_FEED_UPLOADED` with an actor,
`GTFS_FEED_VALIDATED` without one — a queued job decided it, not a person).

The row is left in place rather than deleted: `audit.events` is append-only and already records the
upload, so removing the version would leave the ledger claiming something the audit trail
contradicts. Delete it only if a clean history matters more than that, and expect the audit rows to
outlive it:

```sql
DELETE FROM transit.gtfs_feed_versions WHERE status = 'failed' AND file_name = 'malformed-not-a-feed.zip';
```

---

## Related

- `docs/runbooks/replica-operations.md` — bringing the replica up and down, backup, restore
- `specs/D5_mageride_business_logic.md` — BR-32.1 validation · BR-32.2 activation atomicity ·
  BR-32.3 history and rollback · BR-32.4 the full-feed-at-launch premise
- `specs/D2_mageride_ui_spec.md#scr-ap-016-gtfs-manager` — the seven screen states, including the
  empty one
- `specs/D6_mageride_integration.md` — I-32.1 the feed lifecycle · I-32.2 the launch premise
- `backend/src/Transit.Api/CLAUDE.md` — why each fence in the lifecycle is structural
