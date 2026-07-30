using Dapper;
using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.HotPath.PersistenceWriter.Persistence;
using MageRide.HotPath.PersistenceWriter.Sampling;
using MageRide.HotPath.PersistenceWriter.Summaries;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>
/// Builds persistence-writer-svc's parts against a live Postgres, without a host.
/// </summary>
/// <remarks>
/// The write-path tests drive these directly rather than through the consumer, so an assertion about
/// <i>what a batch wrote</i> cannot be confused with one about <i>whether Kafka delivered it</i> —
/// <c>PersistenceDurabilityTests</c> is what answers the second. Everything built here is the real
/// type against the real hypertable; the only stub is the publisher, and only where a test needs to
/// read what was dead-lettered.
/// </remarks>
internal static class WriterParts
{
    /// <summary>
    /// The kernel's Dapper configuration — snake_case mapping and the DateTimeOffset handlers.
    /// </summary>
    /// <remarks>
    /// <c>AddMageRidePostgres</c> calls this in a running service; a test that builds the parts by
    /// hand has to call it too, or every record whose constructor takes a <c>DateTimeOffset</c> fails
    /// to materialise — Npgsql hands out <c>DateTime</c> for a <c>timestamptz</c> without the handler.
    /// Idempotent and process-global by design.
    /// </remarks>
    static WriterParts() => MageRide.Shared.Persistence.DapperSetup.Configure();

    public static PersistenceWriterOptions Defaults() => new();

    public static IOptions<PersistenceWriterOptions> Wrap(PersistenceWriterOptions? options) =>
        Options.Create(options ?? Defaults());

    public static VehicleContextResolver Resolver(PersistenceWriterOptions? options = null) =>
        new(Wrap(options), TimeProvider.System);

    public static OperationalSampler Sampler(
        IVehicleContextResolver resolver, PersistenceWriterOptions? options = null) =>
        new(resolver, Wrap(options), NullLogger<OperationalSampler>.Instance);

    public static TripSummaryService Summaries(
        PostgresFixture postgres, PersistenceWriterOptions? options = null) =>
        new(new FixtureConnectionFactory(postgres), Wrap(options), NullLogger<TripSummaryService>.Instance);

    /// <summary>The batch writer, wired the way <c>PersistenceWriterApplication</c> wires it.</summary>
    public static PositionBatchWriter Writer(
        PostgresFixture postgres,
        IDeadLetterSink? deadLetters = null,
        PersistenceWriterOptions? options = null,
        IVehicleContextResolver? resolver = null)
    {
        var settings = options ?? Defaults();
        var context = resolver ?? Resolver(settings);

        return new PositionBatchWriter(
            new FixtureConnectionFactory(postgres),
            Sampler(context, settings),
            deadLetters ?? new CollectingDeadLetterSink(),
            Wrap(settings),
            NullLogger<PositionBatchWriter>.Instance);
    }

    /// <summary>Turns samples into the rows the writer takes, resolving no fleet.</summary>
    public static List<PositionRow> Rows(params PositionSample[] samples) =>
        [.. samples.Select(static sample => new PositionRow(sample, sample.FleetId))];

    /// <summary>The minute bucket a fix is stored at, as the sampler computes it.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset instant) =>
        Sampler(Resolver()).Truncate(instant);

    /// <summary>
    /// Inserts the driver, vehicle and (optionally) session a Mode A/B fixture needs.
    /// </summary>
    /// <remarks>
    /// Written as SQL rather than through trip-state-svc's API: this suite has no trip-state-svc and
    /// standing one up would make every write-path test depend on that service's start-up. The rows
    /// are the ones its endpoints produce, and <c>ck_sessions_mode</c> still refuses a Mode C session
    /// if this ever drifts.
    /// </remarks>
    public static async Task<Journey> CreateJourneyAsync(
        PostgresFixture postgres, string mode = "B", DateTimeOffset? startedAt = null, bool active = true)
    {
        await postgres.EnsureMigratedAsync();
        await using var connection = await postgres.OpenAsync();

        var driverId = Guid.CreateVersion7();
        var vehicleId = Guid.CreateVersion7();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@driverId, @phone, 'driver', 'C040 Fixture');
            """,
            new { driverId, phone = $"+9477{Random.Shared.Next(1_000_000, 9_999_999)}" });

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
                (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@vehicleId, @driverId, @plate, 'bus', @mode, 'APPROVED', 'C040 Fixture');
            """,
            new { vehicleId, driverId, plate = $"C040-{Guid.NewGuid():N}"[..16], mode });

        Guid? sessionId = null;

        if (mode is "A" or "B")
        {
            sessionId = Guid.CreateVersion7();

            await connection.ExecuteAsync(
                """
                INSERT INTO trips.sessions (id, vehicle_id, driver_id, mode, state, started_at)
                VALUES (@sessionId, @vehicleId, @driverId, @mode, @state, @startedAt);
                """,
                new
                {
                    sessionId,
                    vehicleId,
                    driverId,
                    mode,
                    state = active ? "ACTIVE" : "COMPLETED",
                    startedAt = startedAt ?? DateTimeOffset.UtcNow.AddHours(-1),
                });
        }

        return new Journey(driverId, vehicleId, sessionId, mode, startedAt ?? DateTimeOffset.UtcNow.AddHours(-1));
    }

    /// <summary>Puts the vehicle in a fleet, so the write path has something to denormalise.</summary>
    public static async Task<Guid> JoinFleetAsync(PostgresFixture postgres, Guid vehicleId)
    {
        await using var connection = await postgres.OpenAsync();

        var fleetId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, email, role, first_name)
            VALUES (@ownerId, @email, 'fleet_owner', 'C040 Fleet');

            INSERT INTO registry.fleets (id, owner_id, name)
            VALUES (@fleetId, @ownerId, @name);

            -- AL-03: a fleet operates Mode A and/or Mode B only, never Mode C.
            INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
            VALUES (@fleetId, @vehicleId, 'A') ON CONFLICT DO NOTHING;
            """,
            new
            {
                fleetId,
                ownerId,
                vehicleId,
                email = $"fleet-{Guid.NewGuid():N}@mageride.test",
                name = $"C040 Fleet {Guid.NewGuid():N}"[..24],
            });

        return fleetId;
    }

    /// <summary>Closes a session the way trip-state-svc closes one.</summary>
    public static async Task EndJourneyAsync(
        PostgresFixture postgres, Guid sessionId, DateTimeOffset endedAt, string reason = "driver_ended")
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE trips.sessions
               SET state = 'COMPLETED', ended_at = @endedAt, end_reason = @reason
             WHERE id = @sessionId;
            """,
            new { sessionId, endedAt, reason });
    }

    /// <summary>Reopens it, the way the US-5.10 restart does — in place, keeping the id.</summary>
    public static async Task RestartJourneyAsync(PostgresFixture postgres, Guid sessionId)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE trips.sessions
               SET state = 'ACTIVE', ended_at = NULL, end_reason = NULL
             WHERE id = @sessionId;
            """,
            new { sessionId });
    }

    /// <summary>A driver, their vehicle, and the tracking session the fixtures write against.</summary>
    internal sealed record Journey(
        Guid DriverId, Guid VehicleId, Guid? SessionId, string Mode, DateTimeOffset StartedAt)
    {
        public Guid Session => SessionId
            ?? throw new InvalidOperationException("This fixture is Mode C and has no tracking session.");
    }

    /// <summary>The kernel's connection factory over the fixture's container.</summary>
    private sealed class FixtureConnectionFactory(PostgresFixture postgres) : INpgsqlConnectionFactory
    {
        public int CommandTimeoutSeconds => 30;

        public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        /// <summary>
        /// The container publishes Postgres directly, so there is no PgBouncer to bypass. Kept
        /// distinct rather than aliased so a caller that needs a session-scoped feature still says so.
        /// </summary>
        public Task<NpgsqlConnection> OpenDirectAsync(CancellationToken cancellationToken = default) =>
            OpenAsync(cancellationToken);
    }

    /// <summary>Records what was dead-lettered, so a test can assert the envelope.</summary>
    internal sealed class CollectingDeadLetterSink : IDeadLetterSink
    {
        private readonly Lock _gate = new();
        private readonly List<(PositionSample Sample, string Reason)> _sent = [];

        public IReadOnlyList<(PositionSample Sample, string Reason)> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        public Task SendAsync(PositionSample sample, string reason, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _sent.Add((sample, reason));
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A sample at <paramref name="point"/>, for a vehicle the fixtures created.</summary>
    public static PositionSample Fix(
        Guid vehicleId,
        GeoPoint point,
        long seq,
        DateTimeOffset sampleTs,
        double? speedMps = 8.5,
        Guid? tripId = null) =>
        new(
            vehicleId,
            sampleTs,
            seq,
            point.Latitude,
            point.Longitude,
            PositionSource.Gt06,
            ReceivedTs: sampleTs.AddMilliseconds(120),
            SpeedMps: speedMps,
            HeadingDeg: 90,
            AccuracyM: 6.0,
            SatCount: 9,
            Mode: "A",
            VehicleType: "bus",
            TripId: tripId);
}
