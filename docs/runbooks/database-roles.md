# Runbook — the least-privilege database roles (C127, ADD §12.6, D-35)

**Applies to:** every deployment. **Owner:** C133 (go-live) for production; C125 for the replica.

Two controls the platform ships are inert until this is done, and both are invisible from inside the
process — nothing logs, nothing 500s, and every test stays green:

| Control | What it is meant to do | What happens without this runbook |
|---|---|---|
| `audit.events` append-only (D-35) | the immutable record of every operator action | the same credential that writes the log can rewrite it |
| Row-level security (ADD §9.5 item 8, §12.6) | fleet scoping enforced in the database | every policy is bypassed; `query-svc` forgetting a `WHERE` is unbounded |

The reason is one sentence: **a Postgres table owner bypasses row-level security unless `FORCE` is
set, and a superuser bypasses it unconditionally and can write any table regardless of `REVOKE`.**
Migration `1305` says so about itself — *"Real immutability is the deployment's job — the service
role must be granted INSERT and SELECT and nothing else (D7' §13)"* — and until C127 no deployment
had done that job.

`security/checks/40-database-privileges.sh` is what tells you whether a given deployment has.

---

## 1. The four roles

All four are `NOLOGIN` group roles created by `db/migrations/2001__least_privilege_roles.sql`. A
deployment creates its own **login** user and grants it membership, which is what makes the D7' §13
90-day credential rotation a new password on a login role rather than a re-grant of the whole
matrix.

| Role | Holds | Used by |
|---|---|---|
| `mageride_app` | DML on the business schemas; `SELECT`+`INSERT` only on `audit.events` | every .NET service |
| `mageride_migrate` | `USAGE, CREATE` on every schema | `MageRide.Migrations` (DbUp) and nothing else |
| `mageride_readonly` | `SELECT` everywhere, writes nowhere | analysts, the observability stack |
| `mageride_fleet_reader` | `SELECT` on `telemetry.*_fleet` only (migration 1804) | query-svc's Epic 13 reads |

`mageride_app` is deliberately **not** an owner. That is the whole point: RLS applies to it, and
`FORCE ROW LEVEL SECURITY` (also set by 2001) means it would apply even if it became one.

---

## 2. Applying the roles

The migration is ordinary and re-runnable. It creates the roles and sets every grant; **it does not
change who connects**, so applying it cannot break a running deployment.

```bash
# with the rest of the migrations
dotnet run --project backend/src/MageRide.Migrations -- --connection "$MIGRATION_CONNECTION"
```

Confirm:

```bash
bash security/run-asvs-checks.sh 40
```

Expect `the least-privilege roles exist` to pass and the three cutover checks to fail. That is the
correct half-done state, and it is loud on purpose.

---

## 3. The cutover

This is the step that closes C127 finding 01. Three commands and a restart.

### 3.1 Create the login users

```sql
-- One login user per authority. Passwords come from Vault (D7' §13), never from this file.
CREATE ROLE mageride_svc     LOGIN PASSWORD :'app_password';
CREATE ROLE mageride_dbup    LOGIN PASSWORD :'migrate_password';
CREATE ROLE mageride_analyst LOGIN PASSWORD :'readonly_password';

GRANT mageride_app       TO mageride_svc;
GRANT mageride_migrate   TO mageride_dbup;
GRANT mageride_readonly  TO mageride_analyst;

-- Membership is not enough on its own for a NOLOGIN group whose privileges must apply without an
-- explicit SET ROLE. `INHERIT` is the default, but say it: a cluster created with
-- `NOINHERIT` as the default would leave every service silently unprivileged.
ALTER ROLE mageride_svc     INHERIT;
ALTER ROLE mageride_dbup    INHERIT;
ALTER ROLE mageride_analyst INHERIT;
```

### 3.2 Point the services at `mageride_svc`

Kubernetes — the connection string is a Vault property, so this is a rotation:

```bash
vault kv patch mageride-production/common-secret \
  ConnectionStrings__Postgres='Host=pgbouncer;Port=6432;Database=mageride;Username=mageride_svc;Password=…'

kubectl -n mageride annotate externalsecret common-secret force-sync=$(date +%s) --overwrite
kubectl -n mageride rollout restart deploy   # every service reads its env at start-up
```

**PgBouncer must accept the new user too.** It authenticates clients itself and then opens a server
connection of its own; a client user it has never heard of is refused before Postgres sees it. Add
`mageride_svc` to its `userlist.txt` (or point `auth_query` at `pg_shadow`) in the same change, or
every service will fail to connect on restart.

Leave `MageRide.Migrations` on `mageride_dbup`: DbUp needs `CREATE`, and a service that held it
could add a table nobody reviewed.

### 3.3 Verify

```bash
bash security/run-asvs-checks.sh 40
```

All of these must pass:

- `'mageride_svc' is not a superuser`
- `audit.events is append-only for the connecting role (D-35)`
- `…and still writable, so the interceptor can record a mutation`
- `row-level security is in force on all 9 policy-bearing tables`

Then a functional pass, because a privilege that is too narrow fails as a 500 on somebody's request
rather than at start-up:

```bash
bash infra/replica/smoke.sh          # replica
bash infra/k8s/verify-readiness.sh   # DOKS
```

---

## 4. Rolling back

Put the previous `Username=` back and restart. Nothing in the cutover is destructive: the grants
stay, the roles stay, and the old owner keeps every privilege it had.

**What to look for if something breaks.** A privilege gap is a `42501 permission denied` in a
service log naming the exact relation. Grant it to `mageride_app` in a **new migration** — never by
hand on one database, or the next environment finds the same gap the same way.

Two that are easy to miss:

- **A new schema.** Migration 2001 iterates a named list. A schema added later needs a line in a
  later migration, or `mageride_app` holds nothing in it and every query is `42501`.
- **A sequence.** `GRANT INSERT` on a table with a `GENERATED … AS IDENTITY` column is not enough on
  older Postgres shapes; 2001 grants `USAGE, SELECT ON ALL SEQUENCES` for exactly this reason.

---

## 5. What this runbook does not fix

**Vault dynamic credentials.** D7' §13 asks for database credentials issued by Vault's database
secrets engine with a short lease, which is strictly better than a static password rotated every 90
days — a leaked credential expires on its own. The role matrix above is the prerequisite for it
(Vault's dynamic role would grant `mageride_app`), and standing up the engine is C133's.

**Per-service roles.** Every service holds `mageride_app`, so a compromised content-svc can read
`iam.users`. Splitting the matrix per bounded context is the natural next step and is out of C127's
scope; it is recorded in `security/remediation-backlog.md` under C127-07.
