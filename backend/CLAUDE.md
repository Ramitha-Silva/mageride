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
- **Central package management** (`backend/Directory.Packages.props`): `<PackageReference>` carries
  no `Version`. Add a `<PackageVersion>` entry there first or the build fails NU1008.
- Verify: `dotnet build && dotnet test`
- Events: transactional outbox tables in Postgres + LISTEN/NOTIFY dispatcher → Redpanda (E-09).
  MassTransit only where the ADD names it (ride-svc saga, R-04 Phase 2) — not a general bus.
- All endpoints must match D3' contracts exactly
