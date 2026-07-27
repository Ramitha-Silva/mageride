using System.Security.Cryptography;
using System.Text;
using Dapper;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Http.Idempotency.Postgres;
using MageRide.Shared.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Tests.Idempotency;

/// <summary>
/// The command log against a real Postgres — the store that actually has to deliver R-14's
/// verbatim replay (ADD §11.13).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCommandLogTests(PostgresFixture postgres)
{
    /// <summary>
    /// The table shape the kernel needs. It differs from D4' §5 in two places — <c>response_body</c>
    /// is <c>JSON</c> rather than <c>JSONB</c>, and <c>response_content_type</c> is new — because
    /// <c>jsonb</c> is a parsed representation and cannot round-trip a body unchanged. Recorded as
    /// a micro-change-set in the C002 handoff.
    /// </summary>
    private const string DdlTemplate =
        """
        CREATE SCHEMA IF NOT EXISTS {0};
        CREATE TABLE IF NOT EXISTS {0}.command_log (
          idempotency_key       TEXT PRIMARY KEY,
          ride_id               UUID,
          actor_type            TEXT NOT NULL,
          actor_id              UUID,
          command               TEXT NOT NULL,
          request_hash          BYTEA NOT NULL,
          response_status       SMALLINT,
          response_body         JSON,
          response_content_type TEXT,
          ts                    TIMESTAMPTZ NOT NULL DEFAULT now());
        """;

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private async Task<PostgresCommandLog> CreateAsync(
        string schema, CommandLogBodyStorage storage = CommandLogBodyStorage.Json, TimeSpan? staleAfter = null)
    {
        var factory = TestHosts.ConnectionFactory(postgres.ConnectionString);

        await using (var connection = await factory.OpenAsync())
        {
            await connection.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, DdlTemplate, schema));
        }

        var options = Options.Create(new CommandLogOptions
        {
            Schema = schema,
            Table = "command_log",
            BodyStorage = storage,
            StaleReservationAfter = staleAfter ?? TimeSpan.FromSeconds(60),
        });

        return new PostgresCommandLog(factory, options, NullLogger<PostgresCommandLog>.Instance);
    }

    [Fact]
    public async Task A_stored_response_replays_byte_for_byte()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var log = await CreateAsync("cl_replay");
        var key = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C1D", "POST /v1/rides/request", Hash("body"), "passenger");

        Assert.Equal(CommandLogOutcome.Reserved, (await log.TryReserveAsync(key)).Outcome);

        // Deliberately awkward JSON: keys out of jsonb's canonical order, insignificant
        // whitespace, a duplicate-looking long key. jsonb would normalise all three away.
        var original = Encoding.UTF8.GetBytes("""{"version":2,"rideId":"8f1c0f6e","z":1,  "state":"Matching"}""");
        await log.CompleteAsync(key.IdempotencyKey, new CommandLogResponse(201, original, "application/json; charset=utf-8"));

        var replay = await log.TryReserveAsync(key);

        Assert.Equal(CommandLogOutcome.Replay, replay.Outcome);
        Assert.Equal(201, replay.Response!.Status);
        Assert.Equal(original, replay.Response.Body);
        Assert.Equal("application/json; charset=utf-8", replay.Response.ContentType);
    }

    [Fact]
    public async Task Bytea_storage_also_replays_byte_for_byte()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var log = await CreateAsync("cl_bytea", CommandLogBodyStorage.Bytea);
        await using (var connection = await TestHosts.ConnectionFactory(postgres.ConnectionString).OpenAsync())
        {
            await connection.ExecuteAsync("ALTER TABLE cl_bytea.command_log ALTER COLUMN response_body TYPE BYTEA USING NULL;");
        }

        var key = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C2E", "POST /v1/fare/pay", Hash("body"), "passenger");
        Assert.Equal(CommandLogOutcome.Reserved, (await log.TryReserveAsync(key)).Outcome);

        var original = Encoding.UTF8.GetBytes("""{"b":1,"a":2}""");
        await log.CompleteAsync(key.IdempotencyKey, new CommandLogResponse(200, original, "application/json"));

        var replay = await log.TryReserveAsync(key);
        Assert.Equal(original, replay.Response!.Body);
    }

    /// <summary>
    /// Shows precisely why the kernel does not default to the D4' §5 <c>JSONB</c> column: the
    /// value comes back semantically equal but not byte for byte, which R-14 asks for.
    /// </summary>
    [Fact]
    public async Task Jsonb_storage_does_not_round_trip_byte_for_byte()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var log = await CreateAsync("cl_jsonb", CommandLogBodyStorage.Jsonb);
        await using (var connection = await TestHosts.ConnectionFactory(postgres.ConnectionString).OpenAsync())
        {
            await connection.ExecuteAsync("ALTER TABLE cl_jsonb.command_log ALTER COLUMN response_body TYPE JSONB USING NULL;");
        }

        var key = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C3F", "POST /v1/rides/request", Hash("body"), "passenger");
        await log.TryReserveAsync(key);

        var original = Encoding.UTF8.GetBytes("""{"version":2,"rideId":"8f1c0f6e",  "state":"Matching"}""");
        await log.CompleteAsync(key.IdempotencyKey, new CommandLogResponse(201, original, "application/json"));

        var replay = await log.TryReserveAsync(key);

        Assert.NotEqual(original, replay.Response!.Body);
    }

    [Fact]
    public async Task A_second_reservation_before_completion_reports_in_progress()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var log = await CreateAsync("cl_inflight");
        var key = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C4G", "POST /v1/rides/request", Hash("body"), "passenger");

        Assert.Equal(CommandLogOutcome.Reserved, (await log.TryReserveAsync(key)).Outcome);
        Assert.Equal(CommandLogOutcome.InProgress, (await log.TryReserveAsync(key)).Outcome);
    }

    [Fact]
    public async Task A_different_payload_under_the_same_key_is_a_mismatch()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var log = await CreateAsync("cl_mismatch");
        var key = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C5H", "POST /v1/rides/request", Hash("first"), "passenger");
        await log.TryReserveAsync(key);

        var other = key with { RequestHash = Hash("second") };
        Assert.Equal(CommandLogOutcome.Mismatch, (await log.TryReserveAsync(other)).Outcome);
    }

    /// <summary>
    /// A process that dies mid-command would otherwise hold the key forever and the client's
    /// retry could never execute.
    /// </summary>
    [Fact]
    public async Task A_stale_reservation_is_reclaimed()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var log = await CreateAsync("cl_stale", staleAfter: TimeSpan.FromSeconds(15));
        var key = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C6J", "POST /v1/rides/request", Hash("body"), "driver");
        await log.TryReserveAsync(key);

        await using (var connection = await TestHosts.ConnectionFactory(postgres.ConnectionString).OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE cl_stale.command_log SET ts = now() - interval '10 minutes' WHERE idempotency_key = @key",
                new { key = key.IdempotencyKey });
        }

        Assert.Equal(CommandLogOutcome.Reserved, (await log.TryReserveAsync(key)).Outcome);
    }

    [Fact]
    public async Task Release_frees_an_uncompleted_key_but_not_a_completed_one()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var log = await CreateAsync("cl_release");

        var pending = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C7K", "POST /v1/rides/request", Hash("body"), "driver");
        await log.TryReserveAsync(pending);
        await log.ReleaseAsync(pending.IdempotencyKey);
        Assert.Equal(CommandLogOutcome.Reserved, (await log.TryReserveAsync(pending)).Outcome);

        var completed = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C8L", "POST /v1/rides/request", Hash("body"), "driver");
        await log.TryReserveAsync(completed);
        await log.CompleteAsync(completed.IdempotencyKey, new CommandLogResponse(200, "{}"u8.ToArray(), "application/json"));
        await log.ReleaseAsync(completed.IdempotencyKey);
        Assert.Equal(CommandLogOutcome.Replay, (await log.TryReserveAsync(completed)).Outcome);
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_reservations_wins()
    {
        Assert.SkipWhen(postgres.SkipReason is not null, postgres.SkipReason ?? string.Empty);

        var log = await CreateAsync("cl_race");
        var key = new CommandLogKey("01HZX3Y8Q9WK4V2N7M5T6B8C9M", "POST /v1/rides/{id}/offer/{d}/accept", Hash("body"), "driver");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => log.TryReserveAsync(key)));

        Assert.Equal(1, outcomes.Count(o => o.Outcome == CommandLogOutcome.Reserved));
        Assert.All(outcomes.Where(o => o.Outcome != CommandLogOutcome.Reserved),
            o => Assert.Equal(CommandLogOutcome.InProgress, o.Outcome));
    }

    [Fact]
    public async Task A_table_name_that_is_not_an_identifier_is_rejected()
    {
        var options = Options.Create(new CommandLogOptions { Schema = "rides", Table = "command_log\"; DROP TABLE x; --" });

        Assert.Throws<ArgumentException>(() => new PostgresCommandLog(
            TestHosts.ConnectionFactory("Host=localhost;Database=x;Username=u;Password=p"),
            options,
            NullLogger<PostgresCommandLog>.Instance));
    }
}
