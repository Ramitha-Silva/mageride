# MageRide.TestKit (C010) — integration-test harness

Stack: .NET 10 class library + **Testcontainers 4.13** + xUnit v3 collection fixtures.
References `MageRide.Migrations` so a test database is migrated by the same DbUp pipeline as
the `migrate` container.

**Verify:** `dotnet test backend/src/MageRide.Shared.Tests -c Release`
(this project has no tests of its own — it is proven by the projects that consume it).

## What it provides

| Fixture | Image | For |
|---|---|---|
| `PostgresFixture` | `timescale/timescaledb-ha:pg16` | the system of record; `EnsureMigratedAsync()` applies `db/migrations/*.sql` |
| `RedisFixture` | `redis:7-alpine` | live geo, locks, rate limits, caches, SignalR backplane (ADD §9.4) |
| `RedpandaFixture` | `redpandadata/redpanda:v24.2.26` | the D6' §2.1 event backbone; `CreateRegistryTopicsAsync()` |
| `EmqxFixture` (C024) | `emqx/emqx:5.8` | the D6' §3 MQTT plane, running the **deployed** broker policy |
| `MinioFixture` (Δ C063) | `minio/minio:latest` | D-36's object store — the bucket every uploaded document goes to |

Every image matches `infra/docker-compose.dev.slim.yml` exactly. **Keep them in step** — a
test that passes against a different server build than the dev stack runs is worth very
little, and the Postgres image in particular is load-bearing: C006's DDL creates a
hypertable and four continuous aggregates, so `postgis/postgis` cannot apply the migration
set at all.

`MinioFixture` sets **`MINIO_KMS_SECRET_KEY`**, and it is not optional: MinIO implements SSE-S3
(`AES256`) through its KMS, so an unconfigured MinIO answers *"Server side encryption specified but
KMS is not configured"* to **every** encrypted PUT — not just to SSE-KMS. Since D-36 requires
encryption at rest and the kernel's store always asks for it, a MinIO without that variable cannot
store a single document. The value must decode to **exactly 32 bytes** or MinIO exits at boot with
"invalid key length", which presents as a container that starts and immediately dies. The dev
compose file sets the same variable for the same reason.

`EmqxFixture` goes further than matching an image: the csproj copies
`infra/deploy/emqx/emqx.conf` and `acl.conf` into the test output and the fixture bind-mounts
them, so an ACL assertion is made against the file the dev stack mounts rather than a copy
that can drift from it. It throws if they are missing — EMQX's shipped defaults allow every
topic, which would turn every ACL test green for the worst possible reason. There is no
Testcontainers module for EMQX, so it is built from the generic `ContainerBuilder`; the wait
strategy is `emqx ctl status`, the same command the compose health check runs, because a
broker whose port is open has not necessarily finished loading its authentication chain and
connecting early produces a `NotAuthorized` indistinguishable from a wrong secret.

## Using it

```csharp
using MageRide.TestKit;

[Collection<PostgresCollection>]                       // NOT [Collection(PostgresCollection.Name)]
public sealed class MyRepositoryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Reads_what_it_wrote()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await postgres.EnsureMigratedAsync();
        await using var connection = await postgres.OpenAsync();
        // ...
    }
}
```

**Use the generic `[Collection<T>]` attribute.** The string form
`[Collection("mageride-postgres")]` resolves the definition inside the test assembly only,
and these definitions live here — every test using the string form fails discovery with
"the following constructor parameters did not have matching fixture data".

## Rules

- **One container per collection, not per test.** `EnsureMigratedAsync()` caches its outcome,
  so a collection pays the ~4 s migration cost once. Namespace your own tables or clean up
  after yourself; the fixture does not reset between tests.
- **Skip, do not fail, when Docker is unreachable** — a developer without a daemon still
  runs the unit tests. CI sets `MAGERIDE_REQUIRE_CONTAINERS=1`, which turns the skip into a
  hard failure so a broken runner cannot report green (see `.github/workflows/README.md`).
- **Do not add a fixture for something the dev stack does not run.** The compose file is the
  contract; a fixture for an image nobody deploys tests a fiction.
- **Migrations go through `MageRide.Migrations.MigrationEngine`**, never a second DbUp
  configuration. Two pipelines would drift on journal table, script ordering or transaction
  scope, and the drift would show up as a deploy failure rather than a test failure.
