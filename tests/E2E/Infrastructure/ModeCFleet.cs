using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Dispatch;
using MageRide.Fanout;
using MageRide.Fare;
using MageRide.Fare.Domain;
using MageRide.Reputation;
using MageRide.Ride;
using MageRide.Shared.Http;
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

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// The Mode C platform, running.
/// </summary>
/// <remarks>
/// <para>
/// Five services on five real sockets — ride-svc, reputation-svc, dispatch-svc, fare-svc and
/// fanout-svc — against a real Postgres, Redis, Redpanda and EMQX, each built through its own
/// <c>XApplication.Build</c>. <b>Every background worker is on</b>: ride-svc's outbox dispatcher and
/// R-04 timer sweep, dispatch-svc's <c>ride.events</c> consumer, offer backstop and timer sweep,
/// both last-will subscriptions, and fanout-svc's consumers. A scenario books a ride and then waits
/// — nothing here reaches in and moves it along.
/// </para>
/// <para>
/// <b>One fleet for the whole assembly.</b> Every scenario class joins
/// <see cref="ModeCCollection"/>, so xUnit runs them one at a time, and starting five services per
/// class would pay that cost a dozen times over. It also matters for correctness: dispatch-svc's
/// <c>ride.events</c> consumer reads from the *earliest* offset by design ("a booking committed
/// while the service was down still has to be dispatched"), so a second fleet with a second
/// consumer group would replay every booking the first one made and go hunting for drivers to
/// offer rides that finished minutes ago.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> trip-state-svc and the tracker plane are C121's — R-01
/// draws the line and this suite does not cross it. iam-svc is absent because a bearer is not what
/// C120 is about (see <see cref="TestTokenIssuer"/>); notification-svc, support-svc and wallet-svc
/// are absent because nothing a Mode C ride does *requires* them to answer — a cash fare posts a
/// driver earning without a ledger hop, and where one of them would be called the fleet leaves the
/// address unset, which is a documented refusal rather than a stub that always says yes.
/// </para>
/// </remarks>
internal sealed class ModeCFleet : IAsyncDisposable
{
    /// <summary>Long enough to satisfy <c>Fare:EstimateTokenKey</c>'s minimum length.</summary>
    public const string FareTokenKey = "mageride-c120-e2e-fare-estimate-key";

    /// <summary>Guards ride-svc's <c>/v1/internal/rides/**</c> until C042 lands a mesh.</summary>
    public const string RideInternalKey = "mageride-c120-e2e-ride-internal-key";

    /// <summary>Guards dispatch-svc's <c>/v1/internal/**</c> — the D-05 penalty ledger fare-svc settles.</summary>
    public const string DispatchInternalKey = "mageride-c120-e2e-dispatch-internal-key";

    /// <summary>Guards fare-svc's <c>POST /v1/fare/calculate</c>, the ride's completion hop.</summary>
    public const string FareInternalKey = "mageride-c120-e2e-fare-internal-key";

    /// <summary>Guards reputation-svc's gRPC block-state service (C033).</summary>
    public const string ReputationInternalKey = "mageride-c120-e2e-reputation-key";

    public const string PhoneHashKey = "mageride-c120-e2e-rider-phone-hash-key";

    public const string OtpPepper = "mageride-c120-e2e-package-otp-pepper";

    /// <summary>
    /// The offer window this fleet runs with, and it is <b>not</b> D5' §3.5's fifteen seconds.
    /// </summary>
    /// <remarks>
    /// Every §11.12 cell that has to be applied to a ride sitting in <c>Offered</c> would otherwise
    /// be racing the R-04 backstop on a loaded build host: the offer expires, the ride bounces to
    /// <c>Matching</c>, and the assertion fails for the one reason that is not about the matrix. The
    /// mechanism is what this suite exercises — dispatch asks for a window, ride-svc stamps the
    /// deadline, the backstop fires on the deadline and not on a sweeping node's clock — and
    /// <c>HappyPathScenario</c> asserts that the stamped deadline is exactly the window dispatch
    /// asked for. The <em>value</em> 15 s is pinned in dispatch-svc's own suite (C034
    /// <c>OfferExpiryTests</c>), which is where a change to D5' §3.5 should break a test.
    /// </remarks>
    public static readonly TimeSpan OfferTtl = TimeSpan.FromSeconds(60);

    /// <summary>US-6A.11's global cascade deadline, at the D5' §3.5 value.</summary>
    public static readonly TimeSpan GlobalDispatchTimeout = TimeSpan.FromSeconds(120);

    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private static ModeCFleet? _shared;

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;
    private static int _pickupCounter;

    /// <summary>
    /// Counts the phone numbers this run has minted. Seeded at random, incremented never-repeating.
    /// </summary>
    /// <remarks>
    /// <c>iam.users.phone</c> is <b>UNIQUE</b>, and <see cref="NextPhone"/> used to draw seven
    /// random digits with nothing stopping two draws landing on the same one. That is the birthday
    /// problem against a nine-million space: a few hundred passengers and package recipients across
    /// one E2E matrix is already a percent or so per run, and what it produces is
    /// <c>23505: duplicate key value violates unique constraint "users_phone_key"</c> thrown from
    /// whichever scenario happened to draw second — a red build with a stack trace pointing at a
    /// fixture rather than at anything under test. The random seed keeps two runs against one
    /// database apart; the increment keeps one run apart from itself. Same shape as
    /// <see cref="_plateCounter"/> beside it, which has always been collision-free for exactly this
    /// reason.
    /// </remarks>
    private static int _phoneCounter = Random.Shared.Next(1_000_000, 8_000_000);

    private readonly WebApplication _ride;
    private readonly WebApplication _reputation;
    private readonly WebApplication _dispatch;
    private readonly WebApplication _fare;
    private readonly WebApplication _fanout;
    private readonly PostgresFixture _postgres;

    private ModeCFleet(
        WebApplication ride,
        WebApplication reputation,
        WebApplication dispatch,
        WebApplication fare,
        WebApplication fanout,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        EmqxFixture emqx)
    {
        _ride = ride;
        _reputation = reputation;
        _dispatch = dispatch;
        _fare = fare;
        _fanout = fanout;
        _postgres = postgres;

        Tokens = tokens;
        Broker = emqx;

        RideClient = NewClient(ride);
        DispatchClient = NewClient(dispatch);
        FareClient = NewClient(fare);
        FanoutBaseAddress = new Uri(BaseAddressOf(fanout));
        Journal = new RideJournal(postgres);
    }

    /// <summary>Talks to ride-svc — the passenger's and the driver's ride surface.</summary>
    public HttpClient RideClient { get; }

    /// <summary>Talks to dispatch-svc — standby, the Job Board, Directional Travel, the penalty ledger.</summary>
    public HttpClient DispatchClient { get; }

    /// <summary>Talks to fare-svc — the estimate, the completion hop and the payment.</summary>
    public HttpClient FareClient { get; }

    /// <summary>Where fanout-svc's <c>/hubs/live</c> is listening.</summary>
    public Uri FanoutBaseAddress { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>The broker a device connects to, so a scenario can register a real last will.</summary>
    public EmqxFixture Broker { get; }

    /// <summary>Prints a ride's whole history when an assertion fails (C120 DoD 3).</summary>
    public RideJournal Journal { get; }

    /// <summary>
    /// ride-svc's container, for the one thing no table can answer: whether its EMQX subscription is
    /// actually live (<see cref="WaitForPresenceSubscriptionAsync"/>).
    /// </summary>
    public IServiceProvider RideServices => _ride.Services;

    // -----------------------------------------------------------------------------------------
    // Lifetime
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The fleet, started at most once per test run.
    /// </summary>
    /// <remarks>
    /// Never disposed on purpose: the five services live as long as the test host, and tearing them
    /// down between classes would cost the consumer-group replay described in the class remarks.
    /// The containers are the TestKit's and the Testcontainers reaper removes them when the process
    /// ends.
    /// </remarks>
    public static async Task<ModeCFleet> SharedAsync(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redpanda);
        ArgumentNullException.ThrowIfNull(emqx);

        if (_shared is not null)
        {
            return _shared;
        }

        await SharedGate.WaitAsync();

        try
        {
            return _shared ??= await StartAsync(postgres, redis, redpanda, emqx);
        }
        finally
        {
            SharedGate.Release();
        }
    }

    private static async Task<ModeCFleet> StartAsync(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    {
        postgres.RequireAvailable();
        redis.RequireAvailable();
        redpanda.RequireAvailable();
        emqx.RequireAvailable();

        await postgres.EnsureMigratedAsync();

        // The five topics D6' §2.1 gives this plane. Created up front rather than left to the
        // broker's auto-creation, so a consumer that subscribes before its first message finds a
        // topic rather than a metadata error it will retry through the first scenario's timeout.
        foreach (var topic in new[]
                 {
                     "ride.events", "dispatch.events", "registry.events", "telemetry.normalized", "audit.events",
                 })
        {
            await redpanda.CreateTopicAsync(topic);
        }

        await ResetAsync(postgres, redis);

        var tokens = new TestTokenIssuer();

        // Start order is the dependency order: ride-svc holds the aggregate, reputation-svc answers
        // the gate, dispatch-svc calls both, fare-svc calls ride-svc and dispatch-svc, and
        // fanout-svc only reads what the others publish.
        var ride = BuildRide(postgres, redpanda, emqx, tokens);
        await ride.StartAsync();

        var reputation = BuildReputation(postgres, redis, redpanda, tokens);
        await reputation.StartAsync();

        var dispatch = BuildDispatch(
            postgres, redis, redpanda, emqx, tokens, BaseAddressOf(ride), GrpcAddressOf(reputation));
        await dispatch.StartAsync();

        var fare = BuildFare(postgres, tokens, BaseAddressOf(ride), BaseAddressOf(dispatch));
        await fare.StartAsync();

        var fanout = BuildFanout(redis, redpanda, emqx, tokens);
        await fanout.StartAsync();

        return new ModeCFleet(ride, reputation, dispatch, fare, fanout, tokens, postgres, emqx);
    }

    public async ValueTask DisposeAsync()
    {
        RideClient.Dispose();
        DispatchClient.Dispose();
        FareClient.Dispose();

        // Reverse start order, so nothing is torn down under an in-flight call from a service that
        // is still running.
        foreach (var app in new[] { _fanout, _fare, _dispatch, _reputation, _ride })
        {
            await app.StopAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------------------------
    // Seeding. Neither ride-svc nor dispatch-svc creates accounts or vehicles — iam-svc and
    // registry-svc do, and neither is in this fleet.
    // -----------------------------------------------------------------------------------------

    public async Task<Passenger> CreatePassengerAsync()
    {
        var id = Guid.NewGuid();
        var phone = NextPhone();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'passenger');",
            new { Id = id, Phone = phone });

        return new Passenger(id, phone, Tokens.Passenger(id));
    }

    /// <summary>A driver with one APPROVED Mode C vehicle, and a wallet the D-08 gate will pass.</summary>
    /// <remarks>
    /// The balance is seeded because D-08's gate refuses a driver from their *second* trip of the
    /// Colombo day, and a scenario that books two rides for one driver would otherwise fail on a
    /// wallet rule that is not what it came to assert. wallet-svc (C046) is the only thing that
    /// moves that balance in production; this writes the read model the gate reads, exactly as
    /// dispatch-svc's own suite does.
    /// </remarks>
    public async Task<Driver> CreateDriverAsync(string vehicleType = "three_wheeler")
    {
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var plate = NextPlate();
        var phone = NextPhone();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role) VALUES (@DriverId, @Phone, 'driver');
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@VehicleId, @DriverId, @Plate, @VehicleType, 'C', 'APPROVED', 'E2E Driver');

            WITH account AS (
              INSERT INTO billing.accounts (owner_type, owner_id, currency, balance_minor)
              VALUES ('driver', @DriverId, 'LKR', 500000)
                  ON CONFLICT (owner_type, owner_id, currency) WHERE owner_id IS NOT NULL
                  DO UPDATE SET balance_minor = EXCLUDED.balance_minor
              RETURNING id)
            INSERT INTO billing.wallets (account_id, balance_minor)
            SELECT id, 500000 FROM account
                ON CONFLICT (account_id) DO UPDATE SET balance_minor = EXCLUDED.balance_minor;
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                Phone = phone,
                Plate = plate,
                VehicleType = vehicleType,
            });

        return new Driver(driverId, vehicleId, plate, phone, Tokens.Driver(driverId));
    }

    /// <summary>Seeds a driver and puts them on standby through dispatch-svc's real route (US-6A.1).</summary>
    public async Task<Driver> CreateOnlineDriverAsync(GeoPoint at, string vehicleType = "three_wheeler")
    {
        var driver = await CreateDriverAsync(vehicleType);
        await GoOnlineAsync(driver, at);

        return driver;
    }

    public async Task GoOnlineAsync(Driver driver, GeoPoint at)
    {
        ArgumentNullException.ThrowIfNull(driver);

        using var response = await PostAsync(
            DispatchClient,
            "/v1/standby/online",
            new
            {
                vehicleId = driver.VehicleId.ToString(),
                position = new { lat = at.Latitude, lng = at.Longitude },
            },
            driver.Bearer);

        await AssertOkAsync(response, $"driver {driver.DriverId} going on standby");
    }

    public async Task GoOfflineAsync(Driver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);

        using var response = await PostAsync(DispatchClient, "/v1/standby/offline", new { }, driver.Bearer);
        await AssertOkAsync(response, $"driver {driver.DriverId} going off standby");
    }

    /// <summary>
    /// A pickup no other ride in this run has used, at least 13 km from every other one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The candidate pool is global — every online driver in <c>dispatch.driver_presence</c> within
    /// the 5 km search radius is a candidate for every ride — and the TestKit's Postgres is shared,
    /// so two rides at one pickup share a pool and "the ride went to the driver I put online" stops
    /// being a property of the scenario. Worse, a driver released back to the pool by an earlier
    /// scenario stays in it for the rest of the run.
    /// </para>
    /// <para>
    /// So each ride is given its own square of a grid laid over Sri Lanka, 0.12° apart — about
    /// 13 km, comfortably more than twice the 5 km radius, and inside the bounding box fare-svc
    /// prices (5.5–10.2 N, 79.2–82.2 E). The grid is finite on purpose: silently wrapping would
    /// reintroduce exactly the overlap it exists to prevent, so running out is an error that names
    /// itself.
    /// </para>
    /// </remarks>
    public static (GeoPoint Pickup, GeoPoint Dropoff) NextPlaces()
    {
        const double Step = 0.12;
        const int Rows = 32;
        const int Columns = 19;

        var index = Interlocked.Increment(ref _pickupCounter) - 1;

        if (index >= Rows * Columns)
        {
            throw new InvalidOperationException(
                $"This run has asked for {index + 1} pickups and the grid holds {Rows * Columns}. Reusing one "
                + "would put two rides in each other's candidate pool; widen the grid or split the suite.");
        }

        var pickup = new GeoPoint(
            6.0 + (Step * (index / Columns)),
            79.6 + (Step * (index % Columns)));

        // The same ~9.5 km hop Colombo Fort → Dehiwala is, in the same direction, so every ride in
        // the suite is priced off the same distance band as the happy path.
        return (pickup, new GeoPoint(pickup.Latitude - 0.083, pickup.Longitude + 0.0225));
    }

    // -----------------------------------------------------------------------------------------
    // The ride, through the surfaces an app has
    // -----------------------------------------------------------------------------------------

    /// <summary>A quote from the real fare-svc, signed with the key ride-svc verifies (D5' §1.1).</summary>
    public async Task<(long AmountMinor, string Token)> QuoteAsync(
        Passenger passenger, GeoPoint from, GeoPoint to, string vehicleType = "three_wheeler", string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/v1/fare/estimate?fromLat={from.Latitude}&fromLng={from.Longitude}" +
            $"&toLat={to.Latitude}&toLng={to.Longitude}&vehicleType={vehicleType}")
            + (kind is null ? string.Empty : $"&kind={kind}");

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", passenger.Bearer);

        using var response = await FareClient.SendAsync(request);
        await AssertOkAsync(response, "fare estimate");

        var body = await ReadJsonAsync(response);

        return (body.GetProperty("amountMinor").GetInt64(), body.GetProperty("fareEstimateToken").GetString()!);
    }

    /// <summary>Books a Mode C ride through ride-svc, quoted by the real fare-svc.</summary>
    public async Task<LiveRide> BookAsync(
        Passenger passenger,
        Driver driver,
        GeoPoint pickup,
        GeoPoint dropoff,
        string vehicleType = "three_wheeler",
        string paymentMethod = "cash",
        string? packageSize = null)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        var kind = packageSize is null ? "passenger" : "package";
        var (_, token) = await QuoteAsync(passenger, pickup, dropoff, vehicleType, kind);

        using var response = await PostAsync(
            RideClient,
            "/v1/rides/request",
            new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                kind,
                pickup = new { lat = pickup.Latitude, lng = pickup.Longitude, address = "E2E pickup" },
                dropoff = new { lat = dropoff.Latitude, lng = dropoff.Longitude, address = "E2E dropoff" },
                vehicleType,
                fareEstimateToken = token,
                paymentMethod,
                packageSize,
                packageDescription = packageSize is null ? null : "One box of mangoes",
                recipientName = packageSize is null ? null : "Kamala",
                recipientPhone = packageSize is null ? null : NextPhone(),
            },
            passenger.Bearer);

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            Assert.Fail($"Booking a ride answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        var body = await ReadJsonAsync(response);

        return new LiveRide(
            body.GetProperty("rideId").GetGuid(),
            passenger,
            driver,
            body.GetProperty("version").GetInt64(),
            pickup,
            dropoff)
        {
            PickupOtp = body.TryGetProperty("pickupOtp", out var otp) && otp.ValueKind is JsonValueKind.String
                ? otp.GetString()
                : null,
        };
    }

    /// <summary>
    /// One driver-side move — <c>arrive</c>, <c>start</c> or <c>complete</c> — echoing the version
    /// the ride is at, and returning where it landed.
    /// </summary>
    public async Task<LiveRide> AdvanceAsync(LiveRide ride, string command)
    {
        ArgumentNullException.ThrowIfNull(ride);

        using var response = await PostAsync(
            RideClient, $"/v1/rides/{ride.RideId}/{command}", new { version = ride.Version }, ride.Driver.Bearer);

        await AssertOkAsync(response, $"{command} on ride {ride.RideId}");

        return ride with { Version = (await ReadJsonAsync(response)).GetProperty("version").GetInt64() };
    }

    /// <summary>The driver taps Accept (ADD §11.11). Raw, so a race can read the status itself.</summary>
    public Task<HttpResponseMessage> AcceptAsync(Guid rideId, Driver driver, Guid offerId, long version)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return PostAsync(
            RideClient,
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offerId.ToString(), version },
            driver.Bearer);
    }

    /// <summary>Either party taps Cancel. The §11.12 matrix — not the caller — decides what it means.</summary>
    /// <param name="version">
    /// What the caller believes the ride is at. Omitted, it is read first — R-02's optimistic
    /// concurrency is what stops a cancel racing an accept, so the route insists a caller say which
    /// ride it thinks it is cancelling, and a scenario that has been waiting on a background worker
    /// does not know. A scenario racing the dispatcher for the <c>Requested</c> state passes the
    /// version the booking answered with, because the extra read is time the dispatcher is using.
    /// </param>
    public async Task<HttpResponseMessage> CancelAsync(
        Guid rideId, string bearer, string reason = "OTHER", long? version = null)
    {
        var at = version ?? (await ReadRideAsync(rideId)).Version;

        return await PostAsync(RideClient, $"/v1/rides/{rideId}/cancel", new { reason, version = at }, bearer);
    }

    /// <summary>
    /// <c>POST /v1/internal/rides/{id}/system-cancel</c>, as dispatch-svc, reputation-svc or
    /// admin-bff would call it.
    /// </summary>
    public Task<HttpResponseMessage> SystemCancelAsync(Guid rideId, string reason) =>
        PostInternalAsync(RideClient, $"/v1/internal/rides/{rideId}/system-cancel", new { reason }, RideInternalKey);

    /// <summary>
    /// Prices a completed ride — fare-svc's <c>POST /v1/fare/calculate</c>, the hop ride-svc's
    /// completion is supposed to make.
    /// </summary>
    /// <remarks>
    /// <b>Called by this suite because nothing in the platform calls it.</b> ride-svc's
    /// <c>CompleteAsync</c> says so at the line that would make the call ("fare-svc's
    /// <c>POST /v1/fare/calculate</c> is C049/C050 — until then this event is the entire
    /// hand-off"), and C049 landed the route without anybody wiring the caller. Every completed ride
    /// therefore stops at <c>PaymentPending</c> with no <c>fares.ride_payments</c> row to pay, which
    /// is a real gap and is recorded in the C120 handoff. Standing in for the missing hop here is
    /// the only way to reach the R-05 terminals at all, and it is done through the same internal
    /// route ride-svc would use rather than by writing a payment row.
    /// </remarks>
    public async Task<FinalFare> PriceAsync(Guid rideId, double? distanceKm = null)
    {
        using var response = await PostInternalAsync(
            FareClient, "/v1/fare/calculate", new { rideId = rideId.ToString(), distanceKm }, FareInternalKey);

        await AssertOkAsync(response, $"pricing ride {rideId}");

        var body = await ReadJsonAsync(response);
        var breakdown = body.GetProperty("breakdown");

        // What the trip alone came to, recomputed from the breakdown fare-svc just returned using
        // fare-svc's own D5' §1.1 formula. `amount_minor` on the payment row is the trip *plus* any
        // D-05 debt settled against it and there is no column that separates the two, so this is the
        // only way to say how much of the charge was the ride — and reproducing the arithmetic with
        // the service's own code means a change to §1.1 moves both sides together.
        var peakPct = breakdown.GetProperty("peakSurchargePct").GetInt32();
        var nightPct = breakdown.GetProperty("nightSurchargePct").GetInt32();

        var trip = FareFormula.Price(
            new Tariff(
                Guid.Empty,
                "e2e",
                breakdown.GetProperty("firstKmMinor").GetInt64(),
                breakdown.GetProperty("perKmMinor").GetInt64(),
                peakPct,
                nightPct,
                FareFormula.Currency,
                DateTimeOffset.UnixEpoch),
            breakdown.GetProperty("distanceKm").GetDouble(),
            isPeak: peakPct > 0,
            isNight: nightPct > 0);

        return new FinalFare(
            body.GetProperty("paymentId").GetGuid(), body.GetProperty("amountMinor").GetInt64(), trip.TotalMinor);
    }

    /// <summary>The passenger pays (D-10). Cash and driver-QR settle on the tap; wallet moves a ledger.</summary>
    public Task<HttpResponseMessage> PayAsync(Guid rideId, Passenger passenger, string method = "cash")
    {
        ArgumentNullException.ThrowIfNull(passenger);

        return PostAsync(
            FareClient, "/v1/fare/pay", new { rideId = rideId.ToString(), method }, passenger.Bearer);
    }

    // -----------------------------------------------------------------------------------------
    // Reading what the platform did
    // -----------------------------------------------------------------------------------------

    public async Task<RideSnapshot> ReadRideAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<RideSnapshot>(
            """
            SELECT state AS State, version AS Version, current_offer_id AS CurrentOfferId,
                   offered_driver_id AS OfferedDriverId, accepted_driver_id AS AcceptedDriverId,
                   accepted_vehicle_id AS AcceptedVehicleId, offer_expires_at AS OfferExpiresAt,
                   terminal_at AS TerminalAt
              FROM rides.rides WHERE id = @RideId;
            """,
            new { RideId = rideId });
    }

    /// <summary>The live offer dispatch-svc placed, or <see langword="null"/> if it has not yet.</summary>
    public async Task<OfferSnapshot?> ReadOfferAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<OfferSnapshot>(
            """
            SELECT id AS Id, driver_id AS DriverId, status AS Status,
                   sent_at AS SentAt, expires_at AS ExpiresAt
              FROM dispatch.offers WHERE ride_id = @RideId ORDER BY sent_at DESC LIMIT 1;
            """,
            new { RideId = rideId });
    }

    /// <summary>The passenger's outstanding D-05 balance, oldest first.</summary>
    public async Task<IReadOnlyList<PenaltySnapshot>> ReadPenaltiesAsync(Guid passengerId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<PenaltySnapshot>(
            """
            SELECT id AS Id, amount_minor::bigint AS AmountMinor, basis AS Basis, status AS Status,
                   original_ride_id AS OriginalRideId, applied_ride_id AS AppliedRideId
              FROM dispatch.cancellation_penalties WHERE passenger_id = @PassengerId
             ORDER BY created_at, id;
            """,
            new { PassengerId = passengerId })];
    }

    /// <summary>The event types a ride put on <c>rides.outbox</c>, oldest first.</summary>
    public async Task<IReadOnlyList<string>> ReadEventsAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<string>(
            "SELECT event_type FROM rides.outbox WHERE aggregate_id = @RideId ORDER BY id;",
            new { RideId = rideId })];
    }

    /// <summary>One outbox payload, as JSON.</summary>
    public async Task<JsonElement> ReadEventPayloadAsync(Guid rideId, string eventType)
    {
        await using var connection = await OpenAsync();

        var payload = await connection.QuerySingleOrDefaultAsync<string>(
            """
            SELECT payload::text FROM rides.outbox
             WHERE aggregate_id = @RideId AND event_type = @EventType ORDER BY id DESC LIMIT 1;
            """,
            new { RideId = rideId, EventType = eventType });

        Assert.True(payload is not null, $"Ride {rideId} never raised '{eventType}'.");

        using var document = JsonDocument.Parse(payload!);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// One <c>dispatch.outbox</c> payload, as JSON.
    /// </summary>
    /// <remarks>
    /// A different table from <see cref="ReadEventPayloadAsync"/> and a different event, despite the
    /// shared name: ride-svc raises <c>offer.created</c> on <c>ride.events</c> to say the aggregate
    /// moved to <c>Offered</c>, and dispatch-svc raises its own on <c>dispatch.events</c> to say who
    /// was chosen and why — which is where DT-08's <c>directionalMatched</c> badge lives.
    /// </remarks>
    public async Task<JsonElement> ReadDispatchEventPayloadAsync(Guid aggregateId, string eventType)
    {
        await using var connection = await OpenAsync();

        var payload = await connection.QuerySingleOrDefaultAsync<string>(
            """
            SELECT payload::text FROM dispatch.outbox
             WHERE aggregate_id = @AggregateId AND event_type = @EventType ORDER BY id DESC LIMIT 1;
            """,
            new { AggregateId = aggregateId, EventType = eventType });

        Assert.True(payload is not null, $"dispatch-svc never raised '{eventType}' for {aggregateId}.");

        using var document = JsonDocument.Parse(payload!);
        return document.RootElement.Clone();
    }

    /// <summary>The ride's unfired <c>rides.timers</c> rows (R-04).</summary>
    public async Task<IReadOnlyList<RideTimerSnapshot>> ReadRideTimersAsync(Guid rideId, string? kind = null)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<RideTimerSnapshot>(
            """
            SELECT id AS Id, kind AS Kind, fire_at AS FireAt, fired_at AS FiredAt
              FROM rides.timers
             WHERE ride_id = @RideId AND fired_at IS NULL AND (@Kind::text IS NULL OR kind = @Kind)
             ORDER BY fire_at;
            """,
            new { RideId = rideId, Kind = kind })];
    }

    /// <summary>
    /// What the driver has earned, as <c>fares.driver_earnings</c> holds it — a per-driver
    /// per-Colombo-day rollup (D-38), not a row per ride.
    /// </summary>
    public async Task<(int Trips, long GrossMinor)> ReadEarningsAsync(Guid driverId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<(int, long)>(
            """
            SELECT COALESCE(sum(trips), 0)::int, COALESCE(sum(gross_minor), 0)::bigint
              FROM fares.driver_earnings WHERE driver_id = @DriverId;
            """,
            new { DriverId = driverId });
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    // -----------------------------------------------------------------------------------------
    // Waiting, and the two clocks a scenario is allowed to move
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Waits for the platform to put the ride in <paramref name="state"/>, and prints the ride's
    /// whole history if it does not.
    /// </summary>
    public async Task<RideSnapshot> WaitForStateAsync(Guid rideId, string state, TimeSpan? within = null)
    {
        var timeout = within ?? TimeSpan.FromSeconds(60);
        var deadline = DateTimeOffset.UtcNow + timeout;

        RideSnapshot snapshot;

        do
        {
            snapshot = await ReadRideAsync(rideId);

            if (string.Equals(snapshot.State, state, StringComparison.Ordinal))
            {
                return snapshot;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail(
            $"Ride {rideId} was still {snapshot.State} after {timeout.TotalSeconds:F0}s waiting for {state}."
            + await Journal.DescribeAsync(rideId));

        return snapshot;
    }

    /// <summary>Waits for dispatch-svc to place an offer and returns it.</summary>
    public async Task<OfferSnapshot> WaitForOfferAsync(Guid rideId, TimeSpan? within = null)
    {
        await WaitForStateAsync(rideId, "Offered", within);

        var offer = await ReadOfferAsync(rideId);

        Assert.True(
            offer is not null,
            $"Ride {rideId} reached Offered with no dispatch.offers row." + await Journal.DescribeAsync(rideId));

        return offer!;
    }

    /// <summary>Waits until <paramref name="condition"/> holds, or fails with the ride's history.</summary>
    public async Task UntilAsync(Guid rideId, Func<Task<bool>> condition, string what, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var timeout = within ?? TimeSpan.FromSeconds(60);
        var deadline = DateTimeOffset.UtcNow + timeout;

        do
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail($"{what} did not happen within {timeout.TotalSeconds:F0}s." + await Journal.DescribeAsync(rideId));
    }

    /// <summary>
    /// Brings a durable timer's deadline into the past so the worker that owns it fires on its very
    /// next tick.
    /// </summary>
    /// <remarks>
    /// <b>This is a clock, not a state fix</b>, and the distinction is the whole reason it is
    /// allowed here (<c>e2e/CLAUDE.md</c>: "never reach into a database to fix state"). Nothing
    /// about the ride, the offer or the matrix is touched — the row this moves is the platform's own
    /// record of *when* something is due, and every scenario that moves one asserts first that the
    /// platform armed it at the window the spec gives (R-16's 60 s / 120 s / 5 min / 10 min, D5' §7's
    /// 5 min, US-6A.11's 120 s). What fires it is the real worker, and what it does when it fires is
    /// the real matrix. The alternative is a suite whose no-show case takes five minutes and whose
    /// COD case takes a day.
    /// </remarks>
    public async Task PullForwardRideTimerAsync(Guid rideId, string kind)
    {
        await using var connection = await OpenAsync();

        var moved = await connection.ExecuteAsync(
            """
            UPDATE rides.timers SET fire_at = now() - interval '1 second'
             WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL;
            """,
            new { RideId = rideId, Kind = kind });

        Assert.True(
            moved > 0,
            $"Ride {rideId} has no live '{kind}' timer to bring forward." + await Journal.DescribeAsync(rideId));
    }

    /// <summary>
    /// Asserts that exactly one live timer of <paramref name="kind"/> is due at the window the spec
    /// gives for it.
    /// </summary>
    /// <remarks>
    /// Always called before a deadline is brought forward, which is what keeps
    /// <see cref="PullForwardRideTimerAsync"/> honest: the window is the platform's arithmetic and
    /// the assertion is on that, not on when the fire happened to land.
    /// </remarks>
    public async Task AssertTimerArmedAsync(Guid rideId, string kind, TimeSpan window)
    {
        var timers = await ReadRideTimersAsync(rideId, kind);

        Assert.True(
            timers.Count == 1,
            $"Ride {rideId} carries {timers.Count} live '{kind}' timers, not one." + await Journal.DescribeAsync(rideId));

        // Slack proportional to the window — a fifth of it, never less than five seconds. The
        // assertion is that the platform armed *this* window rather than one of the others, not that
        // a test process and a database agree about a clock.
        var armed = timers[0].FireAt - DateTimeOffset.UtcNow;
        var slack = TimeSpan.FromSeconds(Math.Max(5, window.TotalSeconds * 0.2));

        Assert.True(
            (armed - window).Duration() < slack,
            $"The '{kind}' timer on ride {rideId} is due in {armed}, not the {window} the spec gives."
            + await Journal.DescribeAsync(rideId));
    }

    /// <summary>
    /// Waits until ride-svc actually holds the <c>veh/+/status</c> subscription (R-15).
    /// </summary>
    /// <remarks>
    /// A refused subscription is the failure that looks like success from the outside: the client
    /// stays connected and simply never hears about a driver going offline. A scenario that dropped
    /// a device before the subscription came up would see the same silence.
    /// </remarks>
    public async Task WaitForPresenceSubscriptionAsync()
    {
        var worker = RideServices.GetRequiredService<MageRide.Ride.Mqtt.VehiclePresenceWorker>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (!worker.IsSubscribed && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(worker.IsSubscribed, "ride-svc's veh/+/status subscription never came up.");
    }

    /// <inheritdoc cref="PullForwardRideTimerAsync"/>
    public async Task PullForwardDispatchTimerAsync(Guid rideId, string kind)
    {
        await using var connection = await OpenAsync();

        var moved = await connection.ExecuteAsync(
            """
            UPDATE dispatch.timers SET fire_at = now() - interval '1 second'
             WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL;
            """,
            new { RideId = rideId, Kind = kind });

        Assert.True(moved > 0, $"Ride {rideId} has no live dispatch '{kind}' timer to bring forward.");
    }

    // -----------------------------------------------------------------------------------------
    // HTTP plumbing
    // -----------------------------------------------------------------------------------------

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, object? body, string? bearer)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        // D3' §0 makes the header mandatory on every POST; omitting it by accident would test the
        // 400 path instead of the route.
        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostInternalAsync(
        HttpClient client, string path, object? body, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        request.Headers.Add("X-MageRide-Internal-Key", apiKey);

        return client.SendAsync(request);
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>The <c>type</c> slug of an RFC 7807 problem, for the negative assertions.</summary>
    public static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await ReadJsonAsync(response);
        var type = problem.TryGetProperty("type", out var value) ? value.GetString() ?? string.Empty : string.Empty;

        return type[(type.LastIndexOf('/') + 1)..];
    }

    private static async Task AssertOkAsync(HttpResponseMessage response, string what)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            Assert.Fail($"{what} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------

    private static WebApplication BuildRide(
        PostgresFixture postgres, RedpandaFixture redpanda, EmqxFixture emqx, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,

                // The container is plain Postgres, not PgBouncer — so the pooled DSN also serves the
                // LISTEN the outbox dispatcher registers (E-09).
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Fare:EstimateTokenKey"] = FareTokenKey,
                ["Ride:InternalApiKey"] = RideInternalKey,
                ["Ride:PhoneHashKey"] = PhoneHashKey,
                ["Ride:OtpPepper"] = OtpPepper,
                ["Ride:OfferTtl"] = OfferTtl.ToString(),
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,

                // On. R-13's whole claim is that an event exists because a transaction committed,
                // and the LISTEN/NOTIFY dispatcher is what puts it on the wire afterwards.
                ["Outbox:DispatcherEnabled"] = "true",

                // On. The no-show, arrival and offline graces are what make §11.12's system rows
                // reachable at all; a fleet that swept by hand would be asserting its own fixture.
                ["Ride:TimersEnabled"] = "true",

                // R-15's subscription, on a real broker running the deployed ACL.
                ["Ride:VehicleStatusEnabled"] = "true",
                ["Mqtt:Host"] = emqx.Host,
                ["Mqtt:Port"] = emqx.Port.ToString(CultureInfo.InvariantCulture),
                ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,

                // Off: a dozen suites share one database and each would otherwise gauge the others'
                // rides. R-20's gauges are asserted in ride-svc's own suite.
                ["Ride:StuckStateMetricsEnabled"] = "false",
            },
            (options, configure) => RideApplication.Build(options, configure));

    private static WebApplication BuildReputation(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",
                ["Reputation:InternalApiKey"] = ReputationInternalKey,

                // 0 = an ephemeral port. D7' §4.2's fixed 5005 would collide with anything else on
                // the build host.
                ["Reputation:GrpcListenPort"] = "0",

                // The counters and the detector are C033's subject, not this suite's; what has to be
                // real here is that the gate is asked and answers.
                ["Reputation:ConsumerEnabled"] = "false",
                ["Reputation:ExpiryWorkerEnabled"] = "false",
                ["Reputation:DetectorEnabled"] = "false",
                ["Reputation:BlockStatusCacheTtl"] = "00:00:00.100",
            },
            (options, configure) => ReputationApplication.Build(options, configure));

    private static WebApplication BuildDispatch(
        PostgresFixture postgres,
        RedisFixture redis,
        RedpandaFixture redpanda,
        EmqxFixture emqx,
        TestTokenIssuer tokens,
        string rideBaseUrl,
        string reputationGrpcUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",
                ["Dispatch:RideServiceBaseUrl"] = rideBaseUrl,
                ["Dispatch:RideServiceInternalKey"] = RideInternalKey,
                ["Dispatch:ReputationGrpcAddress"] = reputationGrpcUrl,
                ["Dispatch:ReputationInternalKey"] = ReputationInternalKey,
                ["Dispatch:InternalApiKey"] = DispatchInternalKey,
                ["Dispatch:OfferTtl"] = OfferTtl.ToString(),
                ["Dispatch:GlobalTimeout"] = GlobalDispatchTimeout.ToString(),

                // A group per run: a previous run's committed offsets would make this one skip the
                // very booking it is waiting for.
                ["Dispatch:ConsumerGroup"] = $"dispatch-svc-e2e-{Guid.NewGuid():N}",

                // The whole loop, on. This is what makes a booking become an offer without anything
                // in this assembly calling IDispatchService.
                ["Dispatch:ConsumerEnabled"] = "true",
                ["Dispatch:PositionConsumerEnabled"] = "true",
                ["Dispatch:ExpiryWorkerEnabled"] = "true",
                ["Dispatch:DispatchTimerWorkerEnabled"] = "true",
                ["Dispatch:KeyspaceNotificationsEnabled"] = "true",
                ["Dispatch:LastWillEnabled"] = "true",
                ["Mqtt:Host"] = emqx.Host,
                ["Mqtt:Port"] = emqx.Port.ToString(CultureInfo.InvariantCulture),
                ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,

                // The gate's local memo is off, so a block state a scenario has just written is seen
                // by the very next candidate build. The gRPC call itself stays real — it is the part
                // that fails open, and therefore the part worth running.
                ["Dispatch:ReputationCacheTtl"] = "00:00:00",

                // Advance bookings and the Driver Level engine are C035's, and neither is on the
                // Mode C on-demand path C120 covers. Left off rather than left to tick under an
                // assertion about a ride nobody scheduled.
                ["Dispatch:ScheduledWorkerEnabled"] = "false",
                ["Dispatch:LevelWorkerEnabled"] = "false",
            },
            (options, configure) => DispatchApplication.Build(options, configure));

    private static WebApplication BuildFare(
        PostgresFixture postgres, TestTokenIssuer tokens, string rideBaseUrl, string dispatchBaseUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Fare:EstimateTokenKey"] = FareTokenKey,
                ["Fare:InternalApiKey"] = FareInternalKey,

                // R-05's one door back into the ride, and D-05's cross-trip settlement. Both are
                // real hops to the running services beside it.
                ["Fare:RideBaseUrl"] = rideBaseUrl,
                ["Fare:RideInternalApiKey"] = RideInternalKey,
                ["Fare:DispatchBaseUrl"] = dispatchBaseUrl,
                ["Fare:DispatchInternalApiKey"] = DispatchInternalKey,

                // `Fare:WalletBaseUrl` is deliberately unset: wallet-svc is not in this fleet, so a
                // `wallet` payment is refused rather than settled against a stub that always agrees.
                // Every scenario here pays cash, which is D5' §8.1's rail that moves no ledger.
                ["Fare:QrNudgeEnabled"] = "false",
            },
            (options, configure) => FareApplication.Build(options, configure));

    private static WebApplication BuildFanout(
        RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Fanout:ConsumerGroup"] = $"fanout-svc-e2e-{Guid.NewGuid():N}",
                ["Fanout:EventsEnabled"] = "true",
                ["Fanout:PresenceEnabled"] = "true",
                ["Fanout:ControlPlaneEnabled"] = "true",

                // Nothing in this fleet writes positions — position-processor-svc is not here — so
                // the pumps would drain empty cells forever. The plane C120 asserts is
                // RideStateChanged, which arrives from `ride.events` and not from a pump.
                ["Fanout:PumpEnabled"] = "false",
                ["Mqtt:Host"] = emqx.Host,
                ["Mqtt:Port"] = emqx.Port.ToString(CultureInfo.InvariantCulture),
                ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,
            },
            (options, configure) => FanoutApplication.Build(options, configure));

    /// <summary>
    /// The four things every service in this fleet is configured with, and the one thing every one
    /// of them has replaced: the bearer handler's signing key.
    /// </summary>
    private static WebApplication Build(
        TestTokenIssuer tokens,
        Dictionary<string, string?> settings,
        Func<WebApplicationOptions, Action<WebApplicationBuilder>, WebApplication> build)
    {
        settings["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json";
        settings["Jwt:Issuer"] = tokens.IssuerName;
        settings["Jwt:RequireHttpsMetadata"] = "false";
        settings["Otel:PrometheusEnabled"] = "false";

        return build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace —
                // and on this suite it is often the fastest way to see which worker did what.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(settings);
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                // PostConfigure so this runs after each service's own AddMageRideAuth. Everything
                // else about validation — RS256 only, lifetime, issuer, and fanout-svc's
                // `access_token` hook — is left exactly as the service configured it.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });
    }

    /// <summary>
    /// Empties the dispatch plane and drains the ride outbox before the fleet starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The TestKit shares one Postgres across every suite in the run and resets nothing, so a
    /// driver left online by dispatch-svc's own tests is a genuine candidate for the first ride
    /// booked here — and the DT-06 scenario, whose subject is that the pool is <em>empty</em>, would
    /// fail for the right reason at the wrong time.
    /// </para>
    /// <para>
    /// <c>rides.outbox</c> is drained rather than emptied, and that one is not cosmetic. Other
    /// suites run with the dispatcher off, so their rows sit undispatched; the moment this fleet
    /// turns it on, every one of them is published and dispatch-svc's consumer dutifully tries to
    /// dispatch a backlog of rides that finished days ago, reserving this run's drivers on the way
    /// through. Marking them dispatched says what is true: as far as this process is concerned they
    /// have already been delivered.
    /// </para>
    /// <para>
    /// Nothing belonging to another bounded context is truncated. <c>rides.rides</c>,
    /// <c>iam.users</c> and <c>fares.*</c> are left exactly as they were — every scenario mints
    /// fresh ids, and emptying another aggregate's table from a harness is the shortcut that later
    /// hides a real foreign-key bug.
    /// </para>
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres, RedisFixture redis)
    {
        await using (var connection = await postgres.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                TRUNCATE dispatch.candidate_scores, dispatch.offers, dispatch.driver_presence,
                         dispatch.timers, dispatch.outbox, dispatch.command_log,
                         dispatch.job_board_intents, dispatch.scheduled_rides,
                         dispatch.driver_levels, dispatch.no_show_events,
                         dispatch.cancellation_penalties, dispatch.directional_filters;
                UPDATE dispatch.directional_config
                   SET theta_max_deg = 45, detour_max_m = 2000, progress_min_m = 250,
                       max_uses_per_day = 2, max_duration_sec = 7200, clear_on_first_trip = false
                 WHERE id = 1;
                DELETE FROM rides.timers WHERE kind = 'offer_expiry';
                UPDATE rides.outbox SET dispatched_at = now() WHERE dispatched_at IS NULL;
                """);
        }

        var configuration = ConfigurationOptions.Parse(redis.ConnectionString);
        configuration.AllowAdmin = true;

        await using var admin = await ConnectionMultiplexer.ConnectAsync(configuration);

        foreach (var endpoint in admin.GetEndPoints())
        {
            await admin.GetServer(endpoint).FlushDatabaseAsync();
        }
    }

    private static HttpClient NewClient(WebApplication app) =>
        new() { BaseAddress = new Uri(BaseAddressOf(app)), Timeout = TimeSpan.FromSeconds(60) };

    private static string BaseAddressOf(WebApplication app) => AddressesOf(app)[0];

    /// <summary>
    /// reputation-svc's HTTP/2-only endpoint. <c>ReputationApplication</c> binds the HTTP/1.1 one
    /// first and the gRPC one second, and <c>IServerAddressesFeature</c> reports them in
    /// <c>Listen</c> order — the only way to tell them apart when both took port 0.
    /// </summary>
    private static string GrpcAddressOf(WebApplication app) => AddressesOf(app)[^1];

    private static IReadOnlyList<string> AddressesOf(WebApplication app) =>
        // Fully qualified: StackExchange.Redis has its own IServer and this file needs both.
        [.. app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses];

    private static string NextPlate() =>
        "WP-E2-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private static string NextPhone() =>
        "+9477" + (Interlocked.Increment(ref _phoneCounter) % 10_000_000).ToString("D7", CultureInfo.InvariantCulture);
}
