# Backend Conventions
- .NET 10, C# 14, Minimal APIs (no MVC controllers)
- One project per service: `src/<Service>.Api/`
- Tests: `src/<Service>.Api.Tests/` (xUnit)
- Persistence: Dapper over Npgsql — parameterised SQL, repository per bounded context,
  NpgsqlTransaction for units of work. NO EF Core / DbContext / LINQ-to-SQL (AL-53).
- Migrations: DbUp/Grate versioned .sql scripts (never `dotnet ef`); D4/server_db_schema.md DDL
  is the source of truth
- Verify: `dotnet build && dotnet test`
- Events: transactional outbox tables in Postgres + LISTEN/NOTIFY dispatcher → Redpanda (E-09).
  MassTransit only where the ADD names it (ride-svc saga, R-04 Phase 2) — not a general bus.
- All endpoints must match D3' contracts exactly
