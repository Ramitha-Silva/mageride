# Backend Conventions
- .NET 10, C# 14, Minimal APIs (no MVC controllers)
- One project per service: `src/<Service>.Api/`
- Tests: `src/<Service>.Api.Tests/` (xUnit)
- Persistence: Dapper over Npgsql — parameterised SQL, repository per bounded context,
  NpgsqlTransaction for units of work. NO EF Core / DbContext / LINQ-to-SQL (AL-53).
- Migrations: DbUp/Grate versioned .sql scripts (never `dotnet ef`); D4/server_db_schema.md DDL
  is the source of truth
- Every service references `src/MageRide.Shared` (C002) and calls `AddMageRideDefaults` /
  `UseMageRideDefaults`: RFC 7807 errors, `Idempotency-Key` replay, Dapper/Npgsql, Redis, Redpanda,
  RS256 auth, `/health/live` + `/health/ready`, OpenTelemetry. Cross-cutting code goes there, not
  into a service.
- **Uploaded bytes go through `AddMageRideObjectStore` / `IObjectStore`** (C002 kernel, Δ C063 D-36):
  S3-compatible object storage (MinIO in dev and on the replica, R2/Wasabi/S3 in production), server
  -side encrypted, presigned reads, NFR-28's expiry enforced by the bucket's own lifecycle rule.
  **Never write uploaded bytes to a service-local directory** — seven services each had their own
  and every one of them lost documents on a pod restart. Unset `Storage__S3__Endpoint` still falls
  back to that directory, and each service says which it got at start-up. Two rules when calling it:
  pass `Retention: null` for anything the platform keeps *serving* (a driver's LankaQR is scanned on
  every ride — expiring it breaks the payment rail), and build the key from ids you minted, never
  from a client filename.
- **Central package management** (`backend/Directory.Packages.props`): `<PackageReference>` carries
  no `Version`. Add a `<PackageVersion>` entry there first or the build fails NU1008.
- **Integration tests use `src/MageRide.TestKit`** (C010) — Testcontainers fixtures for
  Postgres (`timescale/timescaledb-ha:pg16`), Redis, Redpanda and EMQX (C024), all matching
  `infra/docker-compose.dev.slim.yml`; the EMQX one bind-mounts the deployed
  `infra/deploy/emqx/*.conf` so a test asserts against the real broker policy. Reference it, use
  `[Collection<PostgresCollection>]` (the generic form — the string form cannot resolve a
  definition in another assembly), and call `EnsureMigratedAsync()` for a migrated schema. Do not
  hand-roll a container.
- **Add every new project to `backend/MageRide.sln`.** CI runs the solution, not a list of
  projects, so a test project outside it is never executed.
- Verify: `dotnet build && dotnet test`
- Events: transactional outbox tables in Postgres + LISTEN/NOTIFY dispatcher → Redpanda (E-09).
  MassTransit only where the ADD names it (ride-svc saga, R-04 Phase 2) — not a general bus.
- All endpoints must match D3' contracts exactly
