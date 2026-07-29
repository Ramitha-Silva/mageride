using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Reputation;
using MageRide.Ride;
using MageRide.Shared.Fares;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.Dispatch.Tests.Infrastructure;

/// <summary>
/// A running dispatch-svc, a running ride-svc <b>and</b> a running reputation-svc on real sockets,
/// against a real Postgres and a real Redis.
/// </summary>
/// <remarks>
/// <para>
/// All three are built through their own <c>*Application.Build</c>, so the pipelines under test are
/// the ones the processes run. ride-svc is real rather than faked because everything the offer loop
/// has to prove lives in the seam between them: who may write <c>rides.state</c>, whose clock
/// stamps <c>offer_expires_at</c>, and whether <c>offer.created</c> can precede a commit.
/// </para>
/// <para>
/// <b>reputation-svc is real for a different reason.</b> D5' §3.2's block-state gate is a gRPC call
/// on the hot path, and its failure mode is silent: an unreachable reputation-svc, a wrong port or
/// a rejected internal key all fail *open*, so a stub would prove only that the code calls what it
/// calls. Every dispatch test in this assembly therefore runs with the gate genuinely on and
/// answering — which is also what stops the wiring from rotting between here and C036.
/// </para>
/// <para>
/// Background workers are <b>off by default</b>. A sweep or a consumer running underneath an
/// assertion would make "the offer expired at 15 s" indistinguishable from "something expired it";
/// the tests that need a worker turn exactly that one on.
/// </para>
/// </remarks>
internal sealed class DispatchHarness : IAsyncDisposable
{
    /// <summary>Long enough to satisfy <c>Fare:EstimateTokenKey</c>'s minimum length.</summary>
    public const string FareTokenKey = "mageride-c023-test-fare-estimate-key";

    /// <summary>Guards ride-svc's <c>/v1/internal/rides/**</c> until C042 lands a mesh.</summary>
    public const string InternalApiKey = "mageride-c023-test-internal-key";

    /// <summary>Guards reputation-svc's gRPC service and its own internal routes (C033).</summary>
    public const string ReputationInternalKey = "mageride-c034-test-reputation-key";

    /// <summary>Guards dispatch-svc's own <c>/v1/internal/**</c> — the no-show report and the D-05 ledger (C035).</summary>
    public const string DispatchInternalKey = "mageride-c035-test-dispatch-key";

    /// <summary>Colombo Fort — every test's pickup unless it says otherwise.</summary>
    public static readonly GeoPoint Pickup = new(6.9344, 79.8428);

    /// <summary>Dehiwala, ~9 km south. Only the pickup matters to dispatch.</summary>
    public static readonly GeoPoint Dropoff = new(6.8514, 79.8653);

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _rideApp;
    private readonly WebApplication _reputationApp;
    private readonly WebApplication _dispatchApp;
    private readonly PostgresFixture _postgres;
    private readonly string _redisConnectionString;

    private DispatchHarness(
        WebApplication rideApp,
        WebApplication reputationApp,
        WebApplication dispatchApp,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        string redisConnectionString)
    {
        _rideApp = rideApp;
        _reputationApp = reputationApp;
        _dispatchApp = dispatchApp;
        _postgres = postgres;
        _redisConnectionString = redisConnectionString;
        Tokens = tokens;

        Client = NewClient(dispatchApp);
        RideClient = NewClient(rideApp);
        Redis = dispatchApp.Services.GetRequiredService<IConnectionMultiplexer>();
    }

    /// <summary>Talks to dispatch-svc.</summary>
    public HttpClient Client { get; }

    /// <summary>Talks to the real ride-svc standing beside it — the passenger half.</summary>
    public HttpClient RideClient { get; }

    public TestTokenIssuer Tokens { get; }

    public IConnectionMultiplexer Redis { get; }

    public IServiceProvider Services => _dispatchApp.Services;

    public IServiceProvider RideServices => _rideApp.Services;

    /// <summary>The codec ride-svc verifies a booking's quote with.</summary>
    public FareEstimateTokenCodec FareTokens => RideServices.GetRequiredService<FareEstimateTokenCodec>();

    public static async Task<DispatchHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? dispatchSettings = null,
        IDictionary<string, string?>? rideSettings = null,
        IDictionary<string, string?>? reputationSettings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        // The TestKit shares one container per collection and does not reset between tests, so a
        // suite whose subject is "who is in the candidate pool" has to start from an empty pool.
        // Without this, a driver left online by an earlier test is a real candidate for this one's
        // ride, and the tests that assert *nobody* is near enough fail for the right reason at the
        // wrong time.
        await ResetDispatchStateAsync(postgres, redis);

        var tokens = new TestTokenIssuer();

        var rideApp = BuildRideService(postgres, tokens, rideSettings);
        await rideApp.StartAsync();

        var reputationApp = BuildReputationService(postgres, redis, tokens, reputationSettings);
        await reputationApp.StartAsync();

        var dispatchApp = BuildDispatchService(
            postgres, redis, tokens, BaseAddressOf(rideApp), GrpcAddressOf(reputationApp), dispatchSettings);

        await dispatchApp.StartAsync();

        return new DispatchHarness(rideApp, reputationApp, dispatchApp, tokens, postgres, redis.ConnectionString);
    }

    // -----------------------------------------------------------------------------------------
    // Seeding. dispatch-svc creates neither accounts nor vehicles — iam-svc and registry-svc do.
    // -----------------------------------------------------------------------------------------

    /// <summary>Creates the <c>iam.users</c> row a booking's foreign keys need.</summary>
    public async Task<Guid> CreatePassengerAsync()
    {
        var passengerId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'passenger');",
            new { Id = passengerId, Phone = NextPhone() });

        return passengerId;
    }

    /// <summary>
    /// A driver with one vehicle, as registry-svc's <c>POST /v1/vehicles</c> plus the dev approve
    /// path would leave them (C021).
    /// </summary>
    public async Task<SeededDriver> CreateDriverAsync(
        string vehicleType = "three_wheeler", string mode = "C", string status = "APPROVED")
    {
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var plate = NextPlate();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role) VALUES (@DriverId, @Phone, 'driver');
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@VehicleId, @DriverId, @Plate, @VehicleType, @Mode, @Status, 'Test Driver');
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                Phone = NextPhone(),
                Plate = plate,
                VehicleType = vehicleType,
                Mode = mode,
                Status = status,
            });

        return new SeededDriver(driverId, vehicleId, plate, Tokens.Driver(driverId));
    }

    /// <summary>Seeds a driver and puts them on standby at <paramref name="position"/>.</summary>
    public async Task<SeededDriver> CreateOnlineDriverAsync(
        GeoPoint position, string vehicleType = "three_wheeler")
    {
        var driver = await CreateDriverAsync(vehicleType);
        var response = await GoOnlineAsync(driver, position);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return driver;
    }

    /// <summary>
    /// Writes a <c>reputation.block_states</c> row directly, as reputation-svc's own rules would
    /// have left it after three confirmed reports or a run of cancellations (C033, D-04).
    /// </summary>
    /// <remarks>
    /// The state and not the counters: reproducing "three reports" through
    /// <c>ReportVehicle</c> would test C033's rules a second time, and what this component has to
    /// prove is that it reads whatever verdict reputation-svc reached.
    /// </remarks>
    public async Task SetBlockStateAsync(Guid userId, string state, string reason = "reports_delist")
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO reputation.block_states (user_id, state, reason, source)
            VALUES (@UserId, @State, @Reason, 'auto')
                ON CONFLICT (user_id) DO UPDATE
               SET state = EXCLUDED.state, reason = EXCLUDED.reason;
            """,
            new { UserId = userId, State = state, Reason = reason });
    }

    /// <summary>Sets a driver's level (D5' §4.2). reputation-svc is the sole writer in production.</summary>
    public async Task SetDriverLevelAsync(Guid driverId, int level)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO dispatch.driver_levels (driver_id, level) VALUES (@DriverId, @Level)
                ON CONFLICT (driver_id) DO UPDATE SET level = EXCLUDED.level;
            """,
            new { DriverId = driverId, Level = (short)level });
    }

    /// <summary>
    /// Gives a driver a wallet with <paramref name="balanceMinor"/> in it — a <c>billing.accounts</c>
    /// row and its <c>billing.wallets</c> mirror, which is what the D-08 gate reads (§10).
    /// </summary>
    /// <remarks>
    /// No journal postings: the ledger is the master of money (D-09) and wallet-svc (C046) is what
    /// will move it. What this gate needs is the read model, and writing it directly is the only way
    /// to set a balance before that service exists.
    /// </remarks>
    public async Task SetWalletBalanceAsync(Guid driverId, long balanceMinor)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            WITH account AS (
              INSERT INTO billing.accounts (owner_type, owner_id, currency, balance_minor)
              VALUES ('driver', @DriverId, 'LKR', @BalanceMinor)
                  ON CONFLICT (owner_type, owner_id, currency) WHERE owner_id IS NOT NULL
                  DO UPDATE SET balance_minor = EXCLUDED.balance_minor
              RETURNING id)
            INSERT INTO billing.wallets (account_id, balance_minor)
            SELECT id, @BalanceMinor FROM account
                ON CONFLICT (account_id) DO UPDATE SET balance_minor = EXCLUDED.balance_minor;
            """,
            new { DriverId = driverId, BalanceMinor = balanceMinor });
    }

    /// <summary>Blocks a driver for one passenger — <c>safety.blocked_drivers</c> (US-12.10).</summary>
    public async Task BlockDriverAsync(Guid passengerId, Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO safety.blocked_drivers (passenger_id, driver_id) VALUES (@PassengerId, @DriverId)
                ON CONFLICT (passenger_id, driver_id) DO NOTHING;
            """,
            new { PassengerId = passengerId, DriverId = driverId });
    }

    /// <summary>E-03: registry auto-suspends a vehicle whose documents lapsed (migration 0312).</summary>
    public async Task SuspendVehicleAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE registry.vehicles SET dispatch_state = 'DISPATCH_SUSPENDED' WHERE id = @VehicleId;",
            new { VehicleId = vehicleId });
    }

    /// <summary>Ages a driver's presence so D5' §3.2's GPS-freshness gate sees them as stale.</summary>
    public async Task AgePresenceAsync(Guid driverId, TimeSpan by)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE dispatch.driver_presence SET last_seen_at = now() - @By WHERE driver_id = @DriverId;",
            new { DriverId = driverId, By = by });
    }

    /// <summary>The rows <c>dispatch.candidate_scores</c> holds for one ride, best score first.</summary>
    public async Task<IReadOnlyList<ScoreRow>> ReadScoresAsync(Guid rideId)
    {
        await using var connection = await _postgres.OpenAsync();

        return [.. await connection.QueryAsync<ScoreRow>(
            """
            SELECT driver_id AS DriverId, score AS Score, package_size_compatible AS PackageSizeCompatible,
                   breakdown::text AS Breakdown, dispatch_algorithm_version AS Version
              FROM dispatch.candidate_scores WHERE ride_id = @RideId ORDER BY score DESC, driver_id;
            """,
            new { RideId = rideId })];
    }

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    public Task<HttpResponseMessage> GoOnlineAsync(
        SeededDriver driver, GeoPoint position, GeoPoint? driverHome = null, Guid? vehicleId = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return PostAsync(
            "/v1/standby/online",
            new
            {
                vehicleId = (vehicleId ?? driver.VehicleId).ToString(),
                position = new { lat = position.Latitude, lng = position.Longitude },
                driverHome = driverHome is { } home ? new { lat = home.Latitude, lng = home.Longitude } : null,
            },
            driver.Bearer);
    }

    public Task<HttpResponseMessage> GoOfflineAsync(SeededDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return PostAsync("/v1/standby/offline", new { }, driver.Bearer);
    }

    /// <summary>POSTs JSON to dispatch-svc with a fresh <c>Idempotency-Key</c> (D3' §0).</summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? bearer, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return Client.SendAsync(request);
    }

    /// <summary>GETs from dispatch-svc as <paramref name="bearer"/>.</summary>
    public Task<HttpResponseMessage> GetAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return Client.SendAsync(request);
    }

    /// <summary>PUTs JSON to dispatch-svc — the two admin configuration routes.</summary>
    public Task<HttpResponseMessage> PutAsync(string path, object? body, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return Client.SendAsync(request);
    }

    /// <summary>DELETEs from dispatch-svc as <paramref name="bearer"/>.</summary>
    public Task<HttpResponseMessage> DeleteAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return Client.SendAsync(request);
    }

    // -----------------------------------------------------------------------------------------
    // Directional Travel (C036)
    // -----------------------------------------------------------------------------------------

    /// <summary>Sets a Destination Filter through <c>POST /v1/standby/directional</c> (DT-01).</summary>
    public Task<HttpResponseMessage> SetDirectionalAsync(
        SeededDriver driver, GeoPoint destination, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return PostAsync(
            "/v1/standby/directional",
            new { destination = new { lat = destination.Latitude, lng = destination.Longitude }, label },
            driver.Bearer);
    }

    /// <summary>Sets a filter and asserts it took, returning the 201 body.</summary>
    public async Task<JsonElement> SetDirectionalForAsync(
        SeededDriver driver, GeoPoint destination, string? label = null)
    {
        using var response = await SetDirectionalAsync(driver, destination, label);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        return await ReadJsonAsync(response);
    }

    public Task<HttpResponseMessage> GetDirectionalAsync(SeededDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return GetAsync("/v1/standby/directional", driver.Bearer);
    }

    public Task<HttpResponseMessage> ClearDirectionalAsync(SeededDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return DeleteAsync("/v1/standby/directional", driver.Bearer);
    }

    /// <summary>Every activation row for a driver, oldest first — cleared ones included (DT-03).</summary>
    public async Task<IReadOnlyList<DirectionalSnapshot>> ReadDirectionalFiltersAsync(Guid driverId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<DirectionalSnapshot>(
            """
            SELECT id AS Id,
                   ST_Y(destination_geo::geometry) AS DestLat,
                   ST_X(destination_geo::geometry) AS DestLng,
                   label AS Label,
                   expires_at AS ExpiresAt,
                   cleared_at AS ClearedAt,
                   cleared_reason AS ClearedReason,
                   used_date AS UsedDate
              FROM dispatch.directional_filters WHERE driver_id = @DriverId
             ORDER BY created_at, id;
            """,
            new { DriverId = driverId })];
    }

    /// <summary>The live <c>dispatch.timers</c> rows for a driver, by kind.</summary>
    public async Task<IReadOnlyList<DispatchTimerSnapshot>> ReadDriverTimersAsync(Guid driverId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<DispatchTimerSnapshot>(
            """
            SELECT id AS Id, kind AS Kind, fire_at AS FireAt, fired_at AS FiredAt
              FROM dispatch.timers WHERE driver_id = @DriverId ORDER BY kind, fire_at;
            """,
            new { DriverId = driverId })];
    }

    /// <summary>Every <c>dispatch.outbox</c> row for one aggregate, oldest first.</summary>
    public async Task<IReadOnlyList<OutboxSnapshot>> ReadOutboxAsync(Guid aggregateId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<OutboxSnapshot>(
            """
            SELECT event_type AS EventType, aggregate_id AS AggregateId, payload::text AS Payload
              FROM dispatch.outbox WHERE aggregate_id = @AggregateId ORDER BY id;
            """,
            new { AggregateId = aggregateId })];
    }

    /// <summary>
    /// Moves a driver's Directional Travel timers into the past so one sweep fires them, without
    /// making the test wait out a two-hour duration.
    /// </summary>
    /// <remarks>
    /// The filter's own <c>expires_at</c> is left alone on purpose where the caller wants the
    /// <em>reminder</em> path: DT-08's warning is about a filter that is still live.
    /// </remarks>
    public async Task DueDirectionalTimerAsync(Guid driverId, string kind, bool expireFilter = false)
    {
        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE dispatch.timers SET fire_at = now() - interval '1 second' " +
            " WHERE driver_id = @DriverId AND kind = @Kind AND fired_at IS NULL;",
            new { DriverId = driverId, Kind = kind });

        if (expireFilter)
        {
            await connection.ExecuteAsync(
                "UPDATE dispatch.directional_filters SET expires_at = now() - interval '1 second' " +
                " WHERE driver_id = @DriverId AND cleared_at IS NULL;",
                new { DriverId = driverId });
        }
    }

    /// <summary>Fires one <c>dispatch.timers</c> sweep without waiting on the worker's ticker.</summary>
    /// <remarks>
    /// The worker opens its own scope per sweep, so the root provider is the right one to build it
    /// from — the same shape <c>CascadeTests</c> and <c>LastWillTests</c> use.
    /// </remarks>
    public Task<int> SweepDispatchTimersAsync() =>
        ActivatorUtilities
            .CreateInstance<MageRide.Dispatch.Timers.DispatchTimerWorker>(Services)
            .SweepOnceAsync(TestContext.Current.CancellationToken);

    /// <summary>Backdates a driver's activation rows to another Colombo day (D-38).</summary>
    public async Task BackdateDirectionalUsesAsync(Guid driverId, int days)
    {
        await using var connection = await OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE dispatch.directional_filters SET used_date = used_date - @Days WHERE driver_id = @DriverId;",
            new { DriverId = driverId, Days = days });
    }

    /// <summary>Calls one of dispatch-svc's own <c>/v1/internal/**</c> routes with the shared secret.</summary>
    public Task<HttpResponseMessage> InternalAsync(
        HttpMethod method, string path, object? body = null, string? apiKey = DispatchInternalKey)
    {
        ArgumentNullException.ThrowIfNull(method);

        var request = new HttpRequestMessage(method, path);

        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(body ?? new { });
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (apiKey is not null)
        {
            request.Headers.Add("X-MageRide-Internal-Key", apiKey);
        }

        return Client.SendAsync(request);
    }

    // -----------------------------------------------------------------------------------------
    // Scheduled rides, the Job Board and the Driver Level (C035)
    // -----------------------------------------------------------------------------------------

    /// <summary>Books a future ride through <c>POST /v1/rides/schedule</c>.</summary>
    public Task<HttpResponseMessage> ScheduleRideAsync(
        Guid passengerId,
        DateTimeOffset pickupTime,
        GeoPoint? pickup = null,
        GeoPoint? destination = null,
        string vehicleType = "three_wheeler",
        bool includeDestination = true)
    {
        var from = pickup ?? Pickup;
        var to = destination ?? Dropoff;

        return PostAsync(
            "/v1/rides/schedule",
            new
            {
                pickupLat = from.Latitude,
                pickupLng = from.Longitude,

                // AL-36's negative case is "no destination at all", so the two members go missing
                // together rather than arriving as nulls.
                destLat = includeDestination ? to.Latitude : (double?)null,
                destLng = includeDestination ? to.Longitude : (double?)null,
                pickupTime,
                vehicleType,
            },
            Tokens.Passenger(passengerId));
    }

    /// <summary>Books a ride and returns its <c>dispatch.scheduled_rides</c> id.</summary>
    public async Task<Guid> ScheduleRideForAsync(
        Guid passengerId, DateTimeOffset pickupTime, GeoPoint? pickup = null, string vehicleType = "three_wheeler")
    {
        using var response = await ScheduleRideAsync(passengerId, pickupTime, pickup, vehicleType: vehicleType);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        var body = await ReadJsonAsync(response);
        return Guid.Parse(body.GetProperty("scheduledRideId").GetString()!);
    }

    /// <summary>Reads the D-06 Job Board as a driver standing at <paramref name="origin"/>.</summary>
    public Task<HttpResponseMessage> JobBoardAsync(SeededDriver driver, GeoPoint origin, int? radiusM = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/v1/rides/job-board?lat={origin.Latitude}&lng={origin.Longitude}")
            + (radiusM is { } radius ? $"&radius={radius.ToString(CultureInfo.InvariantCulture)}" : string.Empty);

        return GetAsync(path, driver.Bearer);
    }

    /// <summary>Posts intent on a Job Board card (US-6A.5 — not an acceptance).</summary>
    public Task<HttpResponseMessage> PostIntentAsync(SeededDriver driver, Guid scheduledRideId)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return PostAsync($"/v1/rides/job-board/{scheduledRideId}/intent", new { }, driver.Bearer);
    }

    /// <summary>Fires one T-30 sweep without waiting on the worker's ticker.</summary>
    public async Task<int> MaterialiseDueScheduledRidesAsync()
    {
        // Awaited inside the scope: returning the task would dispose the scope — and with it the
        // unit of work the sweep is holding its FOR UPDATE claim on — before the sweep finished.
        await using var scope = Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<MageRide.Dispatch.Scheduling.IScheduledRideService>()
            .MaterialiseDueAsync(CancellationToken.None);
    }

    /// <summary>The scheduled booking as it now stands, straight from the table.</summary>
    public async Task<ScheduledRideSnapshot> ReadScheduledRideAsync(Guid scheduledRideId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<ScheduledRideSnapshot>(
            "SELECT ride_id AS RideId, status AS Status FROM dispatch.scheduled_rides WHERE id = @Id;",
            new { Id = scheduledRideId });
    }

    /// <summary>
    /// Writes a passenger→driver star rating on a completed ride, as whoever collects ratings would
    /// (US-18.1, <c>trips.ratings</c>). The subject id is not read by the level engine — only the
    /// ratee, the direction and the stars are — so it need not be a real ride.
    /// </summary>
    public async Task RateDriverAsync(Guid driverId, Guid passengerId, int stars, int times = 1)
    {
        await using var connection = await _postgres.OpenAsync();

        for (var i = 0; i < times; i++)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO trips.ratings (subject_kind, subject_id, rater_id, ratee_id, stars, direction)
                VALUES ('ride', @SubjectId, @RaterId, @RateeId, @Stars, 'passenger_to_driver');
                """,
                new
                {
                    SubjectId = Guid.NewGuid(),
                    RaterId = passengerId,
                    RateeId = driverId,
                    Stars = (short)stars,
                });
        }
    }

    /// <summary>The driver's level row, straight from the table.</summary>
    public async Task<DriverLevelSnapshot?> ReadDriverLevelAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<DriverLevelSnapshot>(
            """
            SELECT level::int AS Level, rating_points AS RatingPoints,
                   points_awarded_total AS PointsAwardedTotal
              FROM dispatch.driver_levels WHERE driver_id = @DriverId;
            """,
            new { DriverId = driverId });
    }

    /// <summary>Every penalty row for a passenger, oldest first.</summary>
    public async Task<IReadOnlyList<PenaltySnapshot>> ReadPenaltiesAsync(Guid passengerId)
    {
        await using var connection = await _postgres.OpenAsync();

        return [.. await connection.QueryAsync<PenaltySnapshot>(
            """
            SELECT id AS Id, amount_minor::bigint AS AmountMinor, basis AS Basis, status AS Status,
                   applied_ride_id AS AppliedRideId
              FROM dispatch.cancellation_penalties WHERE passenger_id = @PassengerId
             ORDER BY created_at, id;
            """,
            new { PassengerId = passengerId })];
    }

    // -----------------------------------------------------------------------------------------
    // ride-svc, driven directly — the passenger half of the skeleton
    // -----------------------------------------------------------------------------------------

    /// <summary>Books a ride through the real ride-svc and returns its id.</summary>
    /// <param name="packageSize">
    /// <c>S</c> | <c>M</c> | <c>L</c> books a <b>package</b> delivery rather than a passenger ride
    /// (P-06). Needed for the two delivery-only tiers: Δ C037 enforces AL-09's "+truck|mini_truck
    /// for package delivery", so a passenger booking on one of them is now a 400 rather than a ride
    /// nobody could have been carried on. Nothing else about a dispatch round changes — a delivery
    /// and a passenger ride are interchangeable for the daily fee (P-06) and share every gate.
    /// </param>
    public async Task<Guid> RequestRideAsync(
        Guid passengerId,
        string vehicleType = "three_wheeler",
        GeoPoint? pickup = null,
        string? packageSize = null)
    {
        var from = pickup ?? Pickup;
        var kind = packageSize is null ? "passenger" : "package";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rides/request")
        {
            Content = JsonContent.Create(new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                kind,
                pickup = new { lat = from.Latitude, lng = from.Longitude, address = "Colombo Fort" },
                dropoff = new { lat = Dropoff.Latitude, lng = Dropoff.Longitude, address = "Dehiwala" },
                vehicleType,
                fareEstimateToken = FareTokens.Issue(
                    vehicleType, kind, 74_000, 0, 9.2, from, Dropoff),
                paymentMethod = "cash",
                packageSize,
                recipientName = packageSize is null ? null : "Kamala",
                recipientPhone = packageSize is null ? null : NextPhone(),
            }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Tokens.Passenger(passengerId));

        using var response = await RideClient.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        var body = await ReadJsonAsync(response);
        return Guid.Parse(body.GetProperty("rideId").GetString()!);
    }

    /// <summary>
    /// The driver taps Decline, through ride-svc's real route. It is ride-svc that performs
    /// <c>Offered → Matching</c> and emits <c>offer.declined</c>; dispatch only reacts, so a test
    /// that skipped this and released dispatch's own row alone would leave the ride in
    /// <c>Offered</c> and prove nothing about the cascade.
    /// </summary>
    public async Task DeclineOfferAsync(SeededDriver driver, Guid rideId, Guid offerId)
    {
        ArgumentNullException.ThrowIfNull(driver);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/rides/{rideId}/offer/{driver.DriverId}/decline")
        {
            Content = JsonContent.Create(new { offerId = offerId.ToString() }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", driver.Bearer);

        using var response = await RideClient.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The driver taps Accept, through ride-svc's real route (ADD §11.11).</summary>
    public async Task AcceptOfferAsync(SeededDriver driver, Guid rideId, Guid offerId, long version)
    {
        ArgumentNullException.ThrowIfNull(driver);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept")
        {
            Content = JsonContent.Create(new { offerId = offerId.ToString(), version }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", driver.Bearer);

        using var response = await RideClient.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The passenger cancels, through ride-svc's real route. It is the §11.12 matrix there — not
    /// this test — that decides whether a penalty accrues and how much (R-03).
    /// </summary>
    public async Task<HttpResponseMessage> CancelRideAsync(Guid passengerId, Guid rideId, string reason = "OTHER")
    {
        // `version` is not optional on this route: R-02's optimistic concurrency is what stops a
        // cancel racing an accept, so the caller has to say which ride it thinks it is cancelling.
        var current = await ReadRideAsync(rideId);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/rides/{rideId}/cancel")
        {
            Content = JsonContent.Create(new { reason, version = current.Version }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Tokens.Passenger(passengerId));

        return await RideClient.SendAsync(request);
    }

    /// <summary>
    /// One <c>rides.outbox</c> row, parsed as the envelope dispatch-svc's consumer would receive.
    /// </summary>
    /// <remarks>
    /// The wire shape is read from ride-svc's own outbox rather than hand-built, so a test that
    /// drives the handler directly still fails if the two services stop agreeing about the payload
    /// — which is the only thing a fabricated envelope could not catch.
    /// </remarks>
    public async Task<MageRide.Dispatch.Messaging.RideEventEnvelope> ReadRideEventAsync(
        Guid rideId, string eventType)
    {
        await using var connection = await _postgres.OpenAsync();

        var payload = await connection.QuerySingleOrDefaultAsync<string>(
            """
            SELECT payload::text FROM rides.outbox
             WHERE aggregate_id = @RideId AND event_type = @EventType
             ORDER BY id DESC LIMIT 1;
            """,
            new { RideId = rideId, EventType = eventType });

        Assert.NotNull(payload);

        var envelope = MageRide.Dispatch.Messaging.RideEventEnvelope.TryParse(payload);
        Assert.NotNull(envelope);

        return envelope;
    }

    /// <summary>Hands one <c>ride.events</c> envelope to the consumer's reaction table.</summary>
    public async Task HandleRideEventAsync(MageRide.Dispatch.Messaging.RideEventEnvelope envelope)
    {
        await using var scope = Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<MageRide.Dispatch.Messaging.IRideEventHandler>()
            .HandleAsync(envelope, CancellationToken.None);
    }

    /// <summary>
    /// What ride-svc currently holds for a ride, read straight from its table. A read, never a
    /// write: <c>rides.rides</c> is ride-svc's and dispatch-svc must never touch it.
    /// </summary>
    public async Task<RideSnapshot> ReadRideAsync(Guid rideId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<RideSnapshot>(
            """
            SELECT state AS State,
                   current_offer_id AS CurrentOfferId,
                   offered_driver_id AS OfferedDriverId,
                   offer_expires_at AS OfferExpiresAt,
                   version AS Version
              FROM rides.rides WHERE id = @RideId;
            """,
            new { RideId = rideId });
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>
    /// Destroys the whole Redis keyspace mid-test — what R-04 means by "independent of any Redis
    /// TTL". Needs its own admin connection: the kernel's multiplexer does not open one, and that
    /// is deliberate (a service has no business issuing FLUSHDB or CONFIG SET).
    /// </summary>
    public async Task FlushRedisAsync()
    {
        await using var admin = await ConnectAsAdminAsync(_redisConnectionString);

        foreach (var endpoint in admin.GetEndPoints())
        {
            await admin.GetServer(endpoint).FlushDatabaseAsync();
        }
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-DP-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        RideClient.Dispose();

        // Reverse start order: dispatch is the one holding gRPC and HTTP connections to the other
        // two, so stopping it first means neither of them is torn down under an in-flight call.
        await _dispatchApp.StopAsync();
        await _dispatchApp.DisposeAsync();
        await _reputationApp.StopAsync();
        await _reputationApp.DisposeAsync();
        await _rideApp.StopAsync();
        await _rideApp.DisposeAsync();
    }

    // -----------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------

    private static WebApplication BuildRideService(
        PostgresFixture postgres, TestTokenIssuer tokens, IDictionary<string, string?>? settings)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Fare:EstimateTokenKey"] = FareTokenKey,
            ["Ride:InternalApiKey"] = InternalApiKey,
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            ["Otel:PrometheusEnabled"] = "false",
        };

        Merge(overrides, settings);

        return RideApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder => Configure(builder, tokens, overrides));
    }

    /// <summary>
    /// reputation-svc, standing beside the other two so D5' §3.2's gate is genuinely asked on every
    /// candidate build. Its own workers are off — nothing here counts a cancellation; the tests that
    /// need a block state write it.
    /// </summary>
    private static WebApplication BuildReputationService(
        PostgresFixture postgres, RedisFixture redis, TestTokenIssuer tokens, IDictionary<string, string?>? settings)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            ["Otel:PrometheusEnabled"] = "false",
            ["Reputation:InternalApiKey"] = ReputationInternalKey,

            // 0 = an ephemeral port. D7' §4.2's fixed 5005 would collide the moment two test classes
            // ran at once, and this assembly runs several.
            ["Reputation:GrpcListenPort"] = "0",
            ["Reputation:ConsumerEnabled"] = "false",
            ["Reputation:ExpiryWorkerEnabled"] = "false",
            ["Reputation:DetectorEnabled"] = "false",

            // As short as C033's own Range allows, so a status a test wrote straight into
            // reputation.block_states is not shadowed by the cache C033 puts in front of it. Every
            // test mints fresh user ids, so nothing is cached for them before the write in any
            // case; this is belt as well as braces. The gate's own memo is off on the dispatch side
            // for the same reason.
            ["Reputation:BlockStatusCacheTtl"] = "00:00:00.100",
        };

        Merge(overrides, settings);

        return ReputationApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder => Configure(builder, tokens, overrides));
    }

    private static WebApplication BuildDispatchService(
        PostgresFixture postgres,
        RedisFixture redis,
        TestTokenIssuer tokens,
        string rideBaseUrl,
        string reputationGrpcUrl,
        IDictionary<string, string?>? settings)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            ["Otel:PrometheusEnabled"] = "false",
            ["Dispatch:RideServiceBaseUrl"] = rideBaseUrl,
            ["Dispatch:RideServiceInternalKey"] = InternalApiKey,
            ["Dispatch:ReputationGrpcAddress"] = reputationGrpcUrl,
            ["Dispatch:ReputationInternalKey"] = ReputationInternalKey,
            ["Dispatch:InternalApiKey"] = DispatchInternalKey,

            // A test books a ride and fires the T-30 sweep in the same breath, so the floor that
            // stops "scheduled" meaning "now" is lifted here and asserted on its own in
            // SchedulingTests. The 30-minute lead itself is left at the D5' §3.7 value.
            ["Dispatch:ScheduledMinimumLead"] = "00:00:00",

            // The gate's local memo is off so a block state a test has just written is seen by the
            // very next round. reputation-svc's own cache is zeroed for the same reason; what stays
            // real is the gRPC call itself, which is the part that silently fails open.
            ["Dispatch:ReputationCacheTtl"] = "00:00:00",

            // Off unless a test asks: a background sweep or consumer running under an assertion
            // makes "the backstop fired" indistinguishable from "something fired".
            ["Dispatch:ExpiryWorkerEnabled"] = "false",
            ["Dispatch:DispatchTimerWorkerEnabled"] = "false",
            ["Dispatch:ConsumerEnabled"] = "false",
            ["Dispatch:PositionConsumerEnabled"] = "false",
            ["Dispatch:KeyspaceNotificationsEnabled"] = "false",
            ["Dispatch:ScheduledWorkerEnabled"] = "false",
            ["Dispatch:LevelWorkerEnabled"] = "false",
        };

        Merge(overrides, settings);

        return DispatchApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder => Configure(builder, tokens, overrides));
    }

    private static void Configure(
        WebApplicationBuilder builder, TestTokenIssuer tokens, Dictionary<string, string?> overrides)
    {
        // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
        if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
        {
            builder.Logging.ClearProviders();
        }

        builder.Configuration.AddInMemoryCollection(overrides);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // PostConfigure so this runs after the kernel's AddMageRideAuth has built the options.
        // Everything else about validation — RS256 only, lifetime, issuer — is left exactly as the
        // kernel configured it, because that is what is under test.
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure(bearer =>
            {
                bearer.ConfigurationManager = null;
                bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
            });
    }

    private static void Merge(Dictionary<string, string?> into, IDictionary<string, string?>? from)
    {
        if (from is null)
        {
            return;
        }

        foreach (var (key, value) in from)
        {
            into[key] = value;
        }
    }

    /// <summary>
    /// Empties everything dispatch-svc owns, plus the <c>offer_expiry</c> timers it wrote into
    /// <c>rides.timers</c>. <c>rides.rides</c> and <c>iam.users</c> are left alone — every test
    /// mints fresh ids there, and truncating another bounded context's aggregate from a test
    /// harness is the sort of shortcut that later hides a real foreign-key bug.
    /// </summary>
    /// <remarks>
    /// <c>trips.ratings</c> is another bounded context's table and only the <c>subject_kind =
    /// 'ride'</c> rows are removed — those are the ones this suite writes, and the Driver Level
    /// sweep scans the whole table by design (it is the engine's input). A rated driver left behind
    /// by an earlier test is a driver the next test's sweep dutifully recounts, which turns "the
    /// sweep found nothing to do" into an assertion about every test that ran before it.
    /// </remarks>
    /// <remarks>
    /// <c>rides.outbox</c> is drained rather than emptied, and that one is not cosmetic. Most tests
    /// run with ride-svc's dispatcher off, so their <c>ride.requested</c> rows sit undispatched;
    /// the moment a later test turns the dispatcher on, every one of them is published and the
    /// consumer dutifully tries to dispatch a backlog of rides that finished minutes ago —
    /// reserving this test's only driver on the way through. Marking them dispatched says exactly
    /// what is true: as far as this test run is concerned, they have already been delivered.
    /// </remarks>
    private static async Task ResetDispatchStateAsync(PostgresFixture postgres, RedisFixture redis)
    {
        await using (var connection = await postgres.OpenAsync())
        {
            // `dispatch.job_board_intents` is listed explicitly rather than relying on CASCADE: a
            // TRUNCATE that reaches a table nobody named is how a later test starts failing for a
            // reason nobody wrote down. `dispatch.level_config` is a singleton and is reset rather
            // than emptied — migration 0713 seeds it, and an empty one would silently fall back to
            // the compiled defaults instead of exercising the read.
            await connection.ExecuteAsync(
                """
                TRUNCATE dispatch.candidate_scores, dispatch.offers, dispatch.driver_presence,
                         dispatch.timers, dispatch.outbox, dispatch.command_log,
                         dispatch.job_board_intents, dispatch.scheduled_rides,
                         dispatch.driver_levels, dispatch.no_show_events,
                         dispatch.cancellation_penalties, dispatch.directional_filters;
                UPDATE dispatch.level_config
                   SET level_up_threshold = 500, no_show_penalty_points = 0,
                       cancellation_penalty_points = 0, job_board_min_level = 2
                 WHERE id = 1;
                UPDATE dispatch.directional_config
                   SET theta_max_deg = 45, detour_max_m = 2000, progress_min_m = 250,
                       max_uses_per_day = 2, max_duration_sec = 7200, clear_on_first_trip = false
                 WHERE id = 1;
                DELETE FROM rides.timers WHERE kind = 'offer_expiry';
                DELETE FROM trips.ratings WHERE subject_kind = 'ride';
                UPDATE rides.outbox SET dispatched_at = now() WHERE dispatched_at IS NULL;
                """);
        }

        await using var admin = await ConnectAsAdminAsync(redis.ConnectionString);

        foreach (var endpoint in admin.GetEndPoints())
        {
            await admin.GetServer(endpoint).FlushDatabaseAsync();
        }
    }

    private static async Task<ConnectionMultiplexer> ConnectAsAdminAsync(string connectionString)
    {
        var config = ConfigurationOptions.Parse(connectionString);
        config.AllowAdmin = true;

        return await ConnectionMultiplexer.ConnectAsync(config);
    }

    private static string BaseAddressOf(WebApplication app) => AddressesOf(app).First();

    /// <summary>
    /// reputation-svc's HTTP/2-only endpoint. <c>ReputationApplication</c> binds the HTTP/1.1 one
    /// first and the gRPC one second, and <c>IServerAddressesFeature</c> reports them in
    /// <c>Listen</c> order — which is the only way to tell them apart when both took port 0.
    /// </summary>
    private static string GrpcAddressOf(WebApplication app) => AddressesOf(app).Last();

    private static IReadOnlyList<string> AddressesOf(WebApplication app) =>
        // Fully qualified: StackExchange.Redis has its own IServer, and this file needs both.
        [.. app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses];

    private static HttpClient NewClient(WebApplication app) =>
        new() { BaseAddress = new Uri(BaseAddressOf(app)), Timeout = TimeSpan.FromSeconds(60) };

    private static string NextPhone() =>
        "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);
}

/// <summary>A driver plus the one vehicle they were seeded with.</summary>
internal sealed record SeededDriver(Guid DriverId, Guid VehicleId, string Plate, string Bearer);

/// <summary>A row of the R-11 scoring audit, as a test reads it back.</summary>
internal sealed record ScoreRow(
    Guid DriverId, decimal Score, bool? PackageSizeCompatible, string Breakdown, short Version);

/// <summary>The slice of <c>rides.rides</c> a dispatch assertion cares about.</summary>
internal sealed record RideSnapshot(
    string State, Guid? CurrentOfferId, Guid? OfferedDriverId, DateTimeOffset? OfferExpiresAt, long Version);

/// <summary>The slice of <c>dispatch.scheduled_rides</c> the T-30 assertions read (C035).</summary>
internal sealed record ScheduledRideSnapshot(Guid? RideId, string Status);

/// <summary>The slice of <c>dispatch.driver_levels</c> the level-engine assertions read (C035).</summary>
internal sealed record DriverLevelSnapshot(int Level, int RatingPoints, int PointsAwardedTotal);

/// <summary>The slice of <c>dispatch.cancellation_penalties</c> the D-05 assertions read (C035).</summary>
internal sealed record PenaltySnapshot(
    Guid Id, long AmountMinor, string Basis, string Status, Guid? AppliedRideId);

/// <summary>The slice of <c>dispatch.directional_filters</c> the DT-01..DT-04 assertions read (C036).</summary>
internal sealed record DirectionalSnapshot(
    Guid Id,
    double DestLat,
    double DestLng,
    string? Label,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ClearedAt,
    string? ClearedReason,
    DateOnly UsedDate);

/// <summary>A <c>dispatch.timers</c> row, as a test reads it back (C036).</summary>
internal sealed record DispatchTimerSnapshot(Guid Id, string Kind, DateTimeOffset FireAt, DateTimeOffset? FiredAt);

/// <summary>A <c>dispatch.outbox</c> row, as a test reads it back (C036).</summary>
internal sealed record OutboxSnapshot(string EventType, Guid AggregateId, string Payload);
