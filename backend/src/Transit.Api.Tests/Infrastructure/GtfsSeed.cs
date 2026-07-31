using Dapper;
using MageRide.TestKit;

namespace MageRide.Transit.Tests.Infrastructure;

/// <summary>A halt to seed, with the position it sits at.</summary>
internal sealed record SeedStop(string StopId, string Name, double Lat, double Lng);

/// <summary>A trip to seed: its route, its halts in order, and the minute it calls at each.</summary>
internal sealed record SeedTrip(
    string TripId,
    string RouteId,
    string[] StopIds,
    string? ShapeId = null,
    string? Headsign = null,
    short Direction = 0,
    int[]? MinutesFromStart = null);

/// <summary>
/// A GTFS feed, written the way C057's importer will (§18c), plus the feed-version row.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corridor is real.</b> The halts below are the Colombo Fort → Kottawa axis at their actual
/// coordinates, and route 138 is the bus that runs it — so "a corridor with a known direct route"
/// is asserted against a corridor somebody can check on a map rather than against invented numbers
/// where a sign error would look like a pass.
/// </para>
/// <para>
/// Written with SQL rather than by calling C057's importer, which does not exist yet: this suite is
/// about what routing does with a loaded feed.
/// </para>
/// </remarks>
internal sealed class GtfsSeed(PostgresFixture postgres)
{
    private readonly PostgresFixture _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));

    // The Colombo Fort → Kottawa corridor, roughly along High Level Road.
    public static readonly SeedStop Fort = new("FORT", "Colombo Fort", 6.9344, 79.8428);
    public static readonly SeedStop Maradana = new("MRD", "Maradana", 6.9297, 79.8656);
    public static readonly SeedStop Nugegoda = new("NUG", "Nugegoda", 6.8649, 79.8997);
    public static readonly SeedStop Maharagama = new("MHR", "Maharagama", 6.8482, 79.9265);
    public static readonly SeedStop Kottawa = new("KTW", "Kottawa", 6.8410, 79.9653);

    // Off the 138 corridor: served by another route, so it is only reachable with a transfer.
    public static readonly SeedStop Battaramulla = new("BTM", "Battaramulla", 6.8991, 79.9188);

    // 120 km south. A route between these two must never surface for a Colombo corridor, which is
    // what makes "all direct routes" a claim rather than "every route in the feed".
    public static readonly SeedStop Galle = new("GLE", "Galle", 6.0535, 80.2210);
    public static readonly SeedStop Matara = new("MTR", "Matara", 5.9485, 80.5353);

    /// <summary>Every halt these fixtures use.</summary>
    public static readonly SeedStop[] AllStops =
        [Fort, Maradana, Nugegoda, Maharagama, Kottawa, Battaramulla, Galle, Matara];

    /// <summary>Seeds a whole feed and activates it. Returns the feed-version id.</summary>
    public async Task<Guid> ActivateAsync(
        IReadOnlyList<SeedStop>? stops = null,
        IReadOnlyList<(string RouteId, string ShortName, string LongName)>? routes = null,
        IReadOnlyList<SeedTrip>? trips = null,
        IReadOnlyDictionary<string, (double Lat, double Lng)[]>? shapes = null,
        string feedInfoVersion = "2026-07-01")
    {
        var feedVersionId = await LoadAsync(stops, routes, trips, shapes, feedInfoVersion);

        await ActivateAsync(feedVersionId);

        return feedVersionId;
    }

    /// <summary>Seeds a feed and leaves it <c>validated</c> — activation is a separate step.</summary>
    public async Task<Guid> LoadAsync(
        IReadOnlyList<SeedStop>? stops = null,
        IReadOnlyList<(string RouteId, string ShortName, string LongName)>? routes = null,
        IReadOnlyList<SeedTrip>? trips = null,
        IReadOnlyDictionary<string, (double Lat, double Lng)[]>? shapes = null,
        string feedInfoVersion = "2026-07-01")
    {
        stops ??= AllStops;
        routes ??= DefaultRoutes;
        trips ??= DefaultTrips;

        await using var connection = await _postgres.OpenAsync();

        // The GTFS tables are swapped wholesale on activation (AL-54), so a fresh feed replaces
        // whatever was there — exactly what the importer's one-transaction swap leaves behind.
        await connection.ExecuteAsync(
            """
            TRUNCATE transit.gtfs_stop_times, transit.gtfs_trips, transit.gtfs_shapes,
                     transit.gtfs_stops, transit.gtfs_routes RESTART IDENTITY CASCADE;
            """);

        foreach (var stop in stops)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO transit.gtfs_stops (stop_id, name, geo)
                VALUES (@StopId, @Name, ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography);
                """,
                stop);
        }

        foreach (var (routeId, shortName, longName) in routes)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO transit.gtfs_routes (route_id, agency, route_short_name, route_long_name, route_type)
                VALUES (@RouteId, 'SLTB', @ShortName, @LongName, 3);
                """,
                new { RouteId = routeId, ShortName = shortName, LongName = longName });
        }

        foreach (var trip in trips)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO transit.gtfs_trips (trip_id, route_id, service_id, shape_id, direction, trip_headsign)
                VALUES (@TripId, @RouteId, 'WEEKDAY', @ShapeId, @Direction, @Headsign);
                """,
                new { trip.TripId, trip.RouteId, trip.ShapeId, trip.Direction, trip.Headsign });

            for (var index = 0; index < trip.StopIds.Length; index++)
            {
                var minutes = trip.MinutesFromStart is { } offsets && offsets.Length > index
                    ? offsets[index]
                    : (int?)null;

                await connection.ExecuteAsync(
                    """
                    INSERT INTO transit.gtfs_stop_times (trip_id, stop_id, stop_sequence, arr, dep)
                    VALUES (@TripId, @StopId, @Sequence, @Offset, @Offset);
                    """,
                    new
                    {
                        trip.TripId,
                        StopId = trip.StopIds[index],
                        Sequence = index,
                        Offset = minutes is null ? (TimeSpan?)null : TimeSpan.FromMinutes(minutes.Value),
                    });
            }
        }

        foreach (var (shapeId, points) in shapes ?? DefaultShapes)
        {
            for (var index = 0; index < points.Length; index++)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO transit.gtfs_shapes (shape_id, seq, geo)
                    VALUES (@ShapeId, @Seq, ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography);
                    """,
                    new { ShapeId = shapeId, Seq = index, points[index].Lat, points[index].Lng });
            }
        }

        return await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO transit.gtfs_feed_versions
              (file_name, file_size_bytes, sha256, feed_info_version, counts, status, storage_key, uploaded_by)
            VALUES (@FileName, 1024, @Sha, @FeedInfoVersion, '{}'::jsonb, 'validated', @StorageKey, @UploadedBy)
            RETURNING feed_version_id;
            """,
            new
            {
                FileName = $"gtfs-{feedInfoVersion}.zip",
                Sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                FeedInfoVersion = feedInfoVersion,
                StorageKey = $"gtfs/{Guid.NewGuid():N}.zip",
                UploadedBy = await AdminAsync(connection),
            });
    }

    /// <summary>
    /// Makes a feed the active one, exactly as C057's activation does — the status swap and the
    /// <c>NOTIFY</c> in the same transaction.
    /// </summary>
    /// <param name="notify">
    /// False models a notification that never arrived: a dropped LISTEN, a reconnect window, a
    /// PgBouncer in transaction mode. The safety-net poll is what has to cover it.
    /// </param>
    public async Task ActivateAsync(Guid feedVersionId, bool notify = true)
    {
        await using var connection = await _postgres.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // ux_gtfs_feed_one_active admits exactly one, so the incumbent is archived first.
        await connection.ExecuteAsync(
            """
            UPDATE transit.gtfs_feed_versions
               SET status = 'archived', archived_at = now()
             WHERE status = 'active';

            UPDATE transit.gtfs_feed_versions
               SET status = 'active', activated_at = now()
             WHERE feed_version_id = @FeedVersionId;
            """,
            new { FeedVersionId = feedVersionId },
            transaction);

        if (notify)
        {
            await connection.ExecuteAsync("NOTIFY transit_feed_activated;", transaction: transaction);
        }

        await transaction.CommitAsync();
    }

    /// <summary>Archives every feed, leaving the deployment in AL-55's no-coverage state.</summary>
    public async Task ArchiveAllAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE transit.gtfs_feed_versions
               SET status = 'archived', archived_at = COALESCE(archived_at, now())
             WHERE status = 'active';
            NOTIFY transit_feed_activated;
            """);
    }

    /// <summary>
    /// An <c>iam.users</c> row for a back-office operator (Δ C057).
    /// </summary>
    /// <remarks>
    /// <c>transit.gtfs_feed_versions.uploaded_by</c> references it, so an upload by a subject with
    /// no row fails in the database. Every SCR-AP-016 test therefore mints a real user rather than
    /// a bare token.
    /// </remarks>
    public async Task<Guid> CreateUserAsync(string role)
    {
        await using var connection = await _postgres.OpenAsync();

        return await CreateUserAsync(connection, role);
    }

    private static Task<Guid> AdminAsync(Npgsql.NpgsqlConnection connection) =>
        CreateUserAsync(connection, "admin");

    private static async Task<Guid> CreateUserAsync(Npgsql.NpgsqlConnection connection, string role)
    {
        var id = Guid.NewGuid();

        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, @Role);",
            new { Id = id, Phone = $"+9476{Random.Shared.Next(1_000_000, 9_999_999)}", Role = role });

        return id;
    }

    /// <summary>138 runs the corridor; 154 crosses it at Nugegoda; 999 goes nowhere near either.</summary>
    public static readonly (string RouteId, string ShortName, string LongName)[] DefaultRoutes =
    [
        ("R138", "138", "Colombo Fort – Kottawa"),
        ("R154", "154", "Nugegoda – Battaramulla"),
        ("R999", "999", "Galle – Matara"),
    ];

    public static readonly SeedTrip[] DefaultTrips =
    [
        // The full-length working: the direct answer for Fort → Kottawa.
        new("T138-1", "R138", ["FORT", "MRD", "NUG", "MHR", "KTW"], "S138",
            "Kottawa", 0, [0, 10, 30, 40, 55]),
        // A second trip on the same halts at a different time — one pattern, not two.
        new("T138-2", "R138", ["FORT", "MRD", "NUG", "MHR", "KTW"], "S138",
            "Kottawa", 0, [60, 70, 90, 100, 115]),
        // A short-turn working that stops at Nugegoda. Its own pattern, and it must not be
        // mistaken for a ride to Kottawa.
        new("T138-3", "R138", ["FORT", "MRD", "NUG"], "S138", "Nugegoda", 0, [30, 40, 60]),
        // The return direction, so a Kottawa → Fort query is direct too.
        new("T138-R", "R138", ["KTW", "MHR", "NUG", "MRD", "FORT"], "S138R",
            "Colombo Fort", 1, [0, 15, 25, 45, 55]),
        // The transfer partner: Nugegoda is the interchange onto Battaramulla.
        new("T154-1", "R154", ["NUG", "BTM"], "S154", "Battaramulla", 0, [0, 20]),
        // 120 km away, and deliberately carrying no times and no shape — a feed is allowed to
        // omit both, and an option built from it must still be well-formed.
        new("T999-1", "R999", ["GLE", "MTR"], null, "Matara", 0, null),
    ];

    /// <summary>Shapes for the two 138 directions and the 154. Coarse, but real geometry.</summary>
    public static readonly Dictionary<string, (double Lat, double Lng)[]> DefaultShapes = new(StringComparer.Ordinal)
    {
        ["S138"] =
        [
            (6.9344, 79.8428), (6.9297, 79.8656), (6.8649, 79.8997), (6.8482, 79.9265), (6.8410, 79.9653),
        ],
        ["S138R"] =
        [
            (6.8410, 79.9653), (6.8482, 79.9265), (6.8649, 79.8997), (6.9297, 79.8656), (6.9344, 79.8428),
        ],
        ["S154"] = [(6.8649, 79.8997), (6.8991, 79.9188)],
    };
}
