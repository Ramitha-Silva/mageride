# `security/` — the OWASP ASVS L2 review (C127) and the anti-spoof hardening pass (C128)

**Verify:**
- C127 — `bash security/run-asvs-checks.sh && dotnet test tests/Security -c Release`
- C128 — `dotnet test tests/Security -c Release --filter Category=AntiSpoof`

| File | What it is |
|---|---|
| [`asvs-l2-checklist.md`](asvs-l2-checklist.md) | ASVS 4.0.3 L2, chapter by chapter, with per-item evidence |
| [`threat-matrix-coverage.md`](threat-matrix-coverage.md) | every ADD §12.6 row → a tested control or an accepted risk with an owner |
| [`remediation-backlog.md`](remediation-backlog.md) | every finding, with severity, state, owner and date |
| [`anti-spoof-tuning.md`](anti-spoof-tuning.md) | **C128** — what the adversarial corpus, the ACL matrix, the clone/revocation timings and the E-07 population measured, and the three findings they opened |
| [`anti-spoof-corpus-run.md`](anti-spoof-corpus-run.md) | C128 evidence appendix — per-vehicle-type and per-family rates (GENERATED) |
| [`anti-spoof-collusion-run.md`](anti-spoof-collusion-run.md) | C128 evidence appendix — E-07 precision on a realistic population (GENERATED) |
| `run-asvs-checks.sh` | the runner; `--strict` makes a skipped check a failure |
| `asvs-lib.sh` | the four marks, the counters, the optional live targets |
| `checks/10-repository-secrets.sh` | ignore rules, tracked key material, filled-in placeholders, ESO, rotation |
| `checks/20-configuration.sh` | D-30 default, D-31 floor, the mTLS-plane block list, MQTT, the schema's own controls |
| `checks/30-edge-exposure.sh` | the **running** edge — operational surface, internal plane, version gate, JWKS, a forged bearer |
| `checks/40-database-privileges.sh` | the **connecting database role** — the one finding no file can answer |

The executable half lives in [`tests/Security`](../tests/Security/CLAUDE.md): the deny-by-default
RBAC probe over all 444 endpoints, bearer validation across the fleet, the D-36 redaction perimeter,
and — under `Category=AntiSpoof` — the D-18/T-07 position corpus, the MQTT ACL matrix over all three
listeners, and the T-08/T-12/E-07 measurements.

The two GENERATED appendices are rewritten by
`MAGERIDE_ANTISPOOF_DUMP=1 dotnet test tests/Security -c Release --filter Category=AntiSpoof`. Do not
hand-edit them; change the corpus or the population and re-run.

---

## The two halves, and where the line is

`tests/Security` composes all twenty-five services and reads what they **declare** — every
endpoint's authorization metadata, every service's `TokenValidationParameters`, the redaction
guard's place in an HTTP client's handler chain. Exhaustive, fast, cannot flake, and can only ever
see what is in the assemblies.

This directory asks what is not in an assembly. Both C127 HIGH findings were of that kind:

- **The edge's whole security policy was missing from the deployed image.** Every in-process suite
  composed the gateway from its own source directory, where the file was present, so all 605 of its
  own tests passed while the container ran with **no rate limit on any of seventy routes and nothing
  marked D-30 sensitive**.
- **The services connect to Postgres as a superuser**, which makes `audit.events` mutable and every
  row-level-security policy inert. Every migration is correct; the credential is not.

Neither is visible from inside the process. Neither logs anything alarming. That is the argument for
a review that runs against a deployment.

---

## Running it

```bash
bash security/run-asvs-checks.sh            # everything answerable here
bash security/run-asvs-checks.sh --strict   # a skipped check is a failure (release gate)
bash security/run-asvs-checks.sh 40         # one check file
```

**Live checks need a replica and skip without one**, saying what went unasked — a question nobody
asked must not read as a green tick. The edge and database come from
`infra/replica/.env.replica`, or from `MAGERIDE_LIVE_EDGE` when it is set.

**The fence: the replica, never production.** Nothing here discovers a target by itself and nothing
here writes anything. Every live probe is a read or a request the platform refuses before any
handler reaches a database — C118's first live sweep filed two genuine PDPA obligations against its
own operator account by being less careful, and `tests/Contract/Live/LiveRequestPlan.cs` is that
write-up.

---

## Adding a check

- **Put it in the file whose question it answers**, or add `checks/NN-<topic>.sh` — the runner globs
  them in order and each is sourced, so `asvs-lib.sh`'s marks are already in scope.
- **A check that cannot run must `skip_`, never `ok`.** The summary prints every skip under "Not
  asked — these are unknown, not known-good", and `--strict` turns them into failures.
- **Fail on the observed thing, not on a proxy for it.** `40` asks `row_security_active()` rather
  than "does the table have a policy", because those are different questions and only the first one
  is the control. `20.4` asks which Services are `LoadBalancer` rather than grepping for `1883`,
  because the port exists legitimately inside the cluster.
- **Every finding gets a `remediation-backlog.md` entry with an owner and a date.** That is C127's
  fence and it is not negotiable: "noted" is not a resolution.
