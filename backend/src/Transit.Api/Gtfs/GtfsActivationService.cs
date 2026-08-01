using System.Globalization;
using Dapper;
using MageRide.Shared.Errors;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.Transit.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Transit.Gtfs;

/// <summary>US-28.2's Activate, and US-28.3's rollback, which is the same act (BR-32.3).</summary>
public interface IGtfsActivationService
{
    Task<FeedVersionRow> ActivateAsync(Guid feedVersionId, Guid actorId, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IGtfsActivationService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>Two phases, and the fence is which tables each one touches.</b> The staging load takes
/// minutes on a national feed and writes only to <c>transit_staging.*</c>; the swap is one
/// transaction and is the only thing that touches <c>transit.*</c>. BR-32.2's "on any import or
/// swap failure the prior feed stays live" therefore holds by construction rather than by
/// unwinding: a failed import leaves rows in staging that the next activation truncates, and a
/// failed swap rolls back a transaction that had not yet published anything.
/// </para>
/// <para>
/// <b>The swap is a three-way schema rename, not a delete-and-insert.</b> `ALTER TABLE … SET
/// SCHEMA` is a catalogue update — it does not rewrite a row — so the live dataset is replaced in
/// the time it takes to take the locks, whatever the feed's size. Emptying and refilling the live
/// tables instead would leave them empty for the length of the load, which is the one state
/// passengers must never see.
/// </para>
/// <para>
/// <b>Index names are renamed with the tables</b> (the C005 decision `contracts/transit.yaml`
/// records). The two sides carry deliberately different index names — <c>ix_gtfs_*</c> live,
/// <c>ix_staging_gtfs_*</c> staging — and a rename that swapped the tables but not the names would
/// leave a database where migration 1404's `CREATE INDEX IF NOT EXISTS ix_staging_…` matches
/// nothing and builds a second index on every re-run.
/// </para>
/// <para>
/// <b>One activation at a time, by advisory lock.</b> Two operators activating two feeds would
/// otherwise both truncate and load one staging schema, and the swap would publish a dataset that
/// is half of each. The lock is session-scoped, so it spans both phases; that is also why this is
/// the second thing in the service to need <c>OpenDirectAsync</c> — PgBouncer in transaction mode
/// hands the session back between statements and the lock with it.
/// </para>
/// </remarks>
internal sealed class GtfsActivationService(
    INpgsqlConnectionFactory connections,
    IGtfsImporter importer,
    IGtfsObjectStore objects,
    IAuditEventWriter audit,
    IOptions<TransitOptions> options,
    TimeProvider clock,
    ILogger<GtfsActivationService> logger) : IGtfsActivationService
{
    /// <summary>
    /// The advisory-lock key activation serialises on. Arbitrary but fixed: it names a resource
    /// (the staging schema and the live tables), not a row, so it cannot be derived from one.
    /// </summary>
    private const long ActivationLockKey = 0x4754_4653_0000_0001;

    private readonly TransitOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public async Task<FeedVersionRow> ActivateAsync(
        Guid feedVersionId, Guid actorId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenDirectAsync(cancellationToken);

        if (!await TryLockAsync(connection, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "Another GTFS feed activation is in progress. Wait for it to finish and try again.");
        }

        try
        {
            var version = await ReadAsync(connection, feedVersionId, transaction: null, cancellationToken)
                          ?? throw new MageRideException(MageRideErrors.NotFound, "No such GTFS feed version.");

            RequireActivatable(version);

            var summary = await LoadStagingAsync(connection, version, cancellationToken);

            return await SwapAsync(connection, version, actorId, summary, cancellationToken);
        }
        finally
        {
            await UnlockAsync(connection);
        }
    }

    /// <summary>BR-32.2's admission rule, stated once.</summary>
    private static void RequireActivatable(FeedVersionRow version)
    {
        if (version.Status == FeedStatuses.Active)
        {
            throw new MageRideException(
                MageRideErrors.FeedAlreadyActive, "This feed version is already the live dataset.");
        }

        if (!FeedStatuses.IsActivatable(version.Status))
        {
            // Includes `failed`, which BR-32.3 keeps for its report and never lets go live, and
            // `uploaded`/`validating`, which have simply not been judged yet.
            throw new MageRideException(
                MageRideErrors.FeedNotValidated,
                $"A feed version can only be activated once it is validated; this one is '{version.Status}'.");
        }
    }

    private async Task<GtfsImportSummary> LoadStagingAsync(
        NpgsqlConnection connection, FeedVersionRow version, CancellationToken cancellationToken)
    {
        await using var zip = await objects.OpenAsync(version.StorageKey, cancellationToken)
                              ?? throw new MageRideException(
                                  MageRideErrors.InternalError,
                                  "The original zip for this feed version is no longer in storage, so it cannot be "
                                  + "imported. Re-upload the feed (BR-32.3 retains originals for 12 months).");

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var summary = await importer.LoadStagingAsync(connection, zip, cancellationToken);

        logger.LogInformation(
            "Loaded feed {FeedVersionId} into transit_staging in {Elapsed}: {Routes} routes, {Stops} stops, "
            + "{Trips} trips, {StopTimes} stop times, {Shapes} shape points. The live feed is untouched until the swap.",
            version.FeedVersionId,
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            summary.Routes,
            summary.Stops,
            summary.Trips,
            summary.StopTimes,
            summary.Shapes);

        return summary;
    }

    /// <summary>
    /// The one transaction: swap the tables, flip the version rows, audit, and tell transit-svc.
    /// </summary>
    private async Task<FeedVersionRow> SwapAsync(
        NpgsqlConnection connection,
        FeedVersionRow version,
        Guid actorId,
        GtfsImportSummary summary,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Re-read under a row lock. The advisory lock already excludes a second activation in this
        // service; this excludes everything else, and it is the read the audit's `before` is taken
        // from — so what is recorded is the state the swap actually replaced.
        var current = await ReadAsync(connection, version.FeedVersionId, transaction, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.NotFound, "No such GTFS feed version.");

        RequireActivatable(current);

        var outgoing = await connection.QuerySingleOrDefaultAsync<FeedVersionRow>(new CommandDefinition(
            $"SELECT {GtfsFeedVersionRepository.Columns} FROM transit.gtfs_feed_versions WHERE status = 'active' FOR UPDATE;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        await ExecuteAsync(connection, transaction, SwapSql, cancellationToken);

        // The incumbent is archived FIRST: ux_gtfs_feed_one_active is a plain unique index, not a
        // deferred constraint, so it is checked at the end of each statement — setting the new one
        // active first would collide with the feed it is replacing.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE transit.gtfs_feed_versions
               SET status = 'archived', archived_at = @Now
             WHERE status = 'active';

            UPDATE transit.gtfs_feed_versions
               SET status = 'active', activated_at = @Now, archived_at = NULL
             WHERE feed_version_id = @FeedVersionId;
            """,
            new { FeedVersionId = version.FeedVersionId, Now = now },
            transaction,
            cancellationToken: cancellationToken));

        await audit.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                GtfsAuditActions.FeedActivated,
                EntityType: GtfsAuditActions.FeedEntity,
                EntityId: version.FeedVersionId,
                ActorId: actorId,
                Before: outgoing is null
                    ? null
                    : new
                    {
                        feedVersionId = outgoing.FeedVersionId,
                        feedInfoVersion = outgoing.FeedInfoVersion,
                        fileName = outgoing.FileName,
                        status = outgoing.Status,
                    },
                After: new
                {
                    feedVersionId = version.FeedVersionId,
                    feedInfoVersion = version.FeedInfoVersion,
                    fileName = version.FileName,
                    status = FeedStatuses.Active,
                    rolledBack = current.Status == FeedStatuses.Archived,
                    counts = new
                    {
                        routes = summary.Routes,
                        stops = summary.Stops,
                        trips = summary.Trips,
                        stopTimes = summary.StopTimes,
                        shapes = summary.Shapes,
                    },
                }),
            now,
            cancellationToken);

        // Inside the transaction, so it is delivered exactly when — and only if — the swap commits
        // (D6' I-32.1). transit-svc LISTENs on this and reloads within 60 s; its poll is the
        // safety net for a notification that never lands, not the primary trigger.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_notify(@Channel, @Payload);",
            new { Channel = _options.FeedChannel, Payload = version.FeedVersionId.ToString("D") },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "GTFS feed {FeedVersionId} ({FeedInfoVersion}) is live; {Previous} archived. NOTIFY {Channel} fired.",
            version.FeedVersionId,
            version.FeedInfoVersion ?? "no feed_info version",
            outgoing is null ? "no previous feed to be" : outgoing.FeedVersionId.ToString("D"),
            _options.FeedChannel);

        return await ReadAsync(connection, version.FeedVersionId, transaction: null, cancellationToken)
               ?? throw new MageRideException(MageRideErrors.InternalError, "The activated feed version could not be read back.");
    }

    // -----------------------------------------------------------------------------------------
    // The swap
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Three-way rename through a scratch schema, then the index names back to their side.
    /// </summary>
    /// <remarks>
    /// Foreign keys follow the tables rather than the names — they are catalogue references to an
    /// OID — so the ex-staging tables keep referencing each other after landing in
    /// <c>transit</c>, and the ex-live ones keep referencing each other in
    /// <c>transit_staging</c>. That is what migration 1404 means by "pointing WITHIN
    /// transit_staging, never at the live tables": a staging FK aimed at a live table would drag
    /// the live rows through the swap.
    /// </remarks>
    private const string SwapSql = """
        CREATE SCHEMA IF NOT EXISTS transit_swap;

        ALTER TABLE transit.gtfs_stop_times SET SCHEMA transit_swap;
        ALTER TABLE transit.gtfs_trips      SET SCHEMA transit_swap;
        ALTER TABLE transit.gtfs_shapes     SET SCHEMA transit_swap;
        ALTER TABLE transit.gtfs_stops      SET SCHEMA transit_swap;
        ALTER TABLE transit.gtfs_routes     SET SCHEMA transit_swap;

        ALTER TABLE transit_staging.gtfs_routes     SET SCHEMA transit;
        ALTER TABLE transit_staging.gtfs_stops      SET SCHEMA transit;
        ALTER TABLE transit_staging.gtfs_shapes     SET SCHEMA transit;
        ALTER TABLE transit_staging.gtfs_trips      SET SCHEMA transit;
        ALTER TABLE transit_staging.gtfs_stop_times SET SCHEMA transit;

        ALTER TABLE transit_swap.gtfs_routes     SET SCHEMA transit_staging;
        ALTER TABLE transit_swap.gtfs_stops      SET SCHEMA transit_staging;
        ALTER TABLE transit_swap.gtfs_shapes     SET SCHEMA transit_staging;
        ALTER TABLE transit_swap.gtfs_trips      SET SCHEMA transit_staging;
        ALTER TABLE transit_swap.gtfs_stop_times SET SCHEMA transit_staging;

        DROP SCHEMA transit_swap;

        DO $swap$
        DECLARE
          rename RECORD;
        BEGIN
          FOR rename IN
            SELECT * FROM (VALUES
              ('transit',         'ix_staging_gtfs_trips_route',      'ix_gtfs_trips_route'),
              ('transit',         'ix_staging_gtfs_stops_geo',        'ix_gtfs_stops_geo'),
              ('transit',         'ix_staging_gtfs_stop_times_stop',  'ix_gtfs_stop_times_stop'),
              ('transit_staging', 'ix_gtfs_trips_route',              'ix_staging_gtfs_trips_route'),
              ('transit_staging', 'ix_gtfs_stops_geo',                'ix_staging_gtfs_stops_geo'),
              ('transit_staging', 'ix_gtfs_stop_times_stop',          'ix_staging_gtfs_stop_times_stop')
            ) AS t(schema_name, old_name, new_name)
          LOOP
            IF EXISTS (
              SELECT 1
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname = rename.schema_name
                 AND c.relname = rename.old_name
                 AND c.relkind = 'i')
            THEN
              EXECUTE format('ALTER INDEX %I.%I RENAME TO %I',
                             rename.schema_name, rename.old_name, rename.new_name);
            END IF;
          END LOOP;
        END
        $swap$;
        """;

    // -----------------------------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------------------------

    private async Task<bool> TryLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + _options.Gtfs.ActivationLockWait;

        while (true)
        {
            var acquired = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(@Key);",
                new { Key = ActivationLockKey },
                cancellationToken: cancellationToken));

            if (acquired)
            {
                return true;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return false;
            }

            // Polled rather than blocking on pg_advisory_lock: a caller that waits forever is a
            // request that never answers, and the operator has an Activate button they can press
            // again.
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static async Task UnlockAsync(NpgsqlConnection connection)
    {
        // Never cancelled: dropping the lock is what lets the next operator in, and the session
        // closing would release it anyway — but only after the pool notices.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_unlock(@Key);", new { Key = ActivationLockKey }));
    }

    private static async Task<FeedVersionRow?> ReadAsync(
        NpgsqlConnection connection,
        Guid feedVersionId,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<FeedVersionRow>(new CommandDefinition(
            string.Create(
                CultureInfo.InvariantCulture,
                $"SELECT {GtfsFeedVersionRepository.Columns} FROM transit.gtfs_feed_versions WHERE feed_version_id = @FeedVersionId{(transaction is null ? string.Empty : " FOR UPDATE")};"),
            new { FeedVersionId = feedVersionId },
            transaction,
            cancellationToken: cancellationToken));

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
