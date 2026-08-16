using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Content;
using MageRide.Dispatch;
using MageRide.Fanout;
using MageRide.Fare;
using MageRide.Iam;
using MageRide.Notification;
using MageRide.PublicBff;
using MageRide.Reputation;
using MageRide.Ride;
using MageRide.Safety;
using MageRide.Shared.Http;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// The proxy-booking, package-delivery and web-subview platform, running.
/// </summary>
/// <remarks>
/// <para>
/// Ten services on ten real sockets, against a real Postgres, Redis and Redpanda, each built through
/// its own <c>XApplication.Build</c>, plus one thing that is not a MageRide component at all: a real
/// HTTP endpoint speaking Fit SMS's REST shape (<see cref="SmsGateway"/>). <b>Every background
/// worker is on</b> — ride-svc's outbox dispatcher, its R-04 timer sweep and the
/// <c>location_request</c> expiry pass that rides in it, dispatch-svc's whole loop, notification-svc's
/// four consumers and its delivery worker, and fanout-svc's control plane. A scenario acts and then
/// waits.
/// </para>
/// <para>
/// <b>Why iam-svc is here when C120 and C121 leave it out.</b> Their reason was that a bearer is not
/// what those components are about. It is not the reason iam-svc is in this one: P-03's
/// <c>GET /v1/users/lookup</c> is the *registration oracle* that decides whether a proxy booking
/// takes the FCM round-trip or AL-45's SMS, and ride-svc answers the entire
/// <c>/v1/location-requests</c> family <c>503</c> when it cannot reach it — deliberately, because a
/// null object guessing "unregistered" would SMS a stranger. So the branch every proxy scenario turns
/// on does not exist without it. The bearers are still <see cref="TestTokenIssuer"/>'s, exactly as in
/// the other two fleets: what this fleet uses iam-svc for is one internal read, and C025's walking
/// skeleton is still the only place a real iam token crosses into a real service.
/// </para>
/// <para>
/// <b>Why the SMS gateway is allowed to be a stand-in.</b> The suite's fence is that a scenario
/// drives the platform through a surface an app, a device or a peer service has. An SMS recipient's
/// surface is the message, and the message leaves the platform through a Sri Lankan gateway this
/// suite is never going to dial. AL-44/AL-45 make the share token mint-and-SMS and nothing else —
/// notification-svc's <c>MintedLink</c> has no token member and no contract in that assembly can
/// carry one out — so a scenario that read <c>safety.trip_share_tokens</c> would be asserting about a
/// page no recipient could have reached. Reading it out of the message the platform actually composed
/// is the only honest way in, and it is the same choice C121 made when <c>TrackerDevice</c> writes
/// the bytes firmware would write.
/// </para>
/// <para>
/// <b>This fleet resets nothing, and that is a decision.</b> C120 truncates the dispatch plane and
/// flushes Redis at start-up because the DT-06 scenario's subject is an <em>empty</em> candidate
/// pool. Nothing here has that shape, and the three fleets in this assembly are never disposed — so a
/// reset performed by whichever collection xUnit happens to run second would be pulling the floor out
/// from under services that are still running. What C122 needs instead is that its rides never share
/// a candidate pool with anybody else's, and it gets that from the same 32 × 19 grid C120 walks
/// (<see cref="ModeCFleet.NextPlaces"/>) rather than from a truncate: the counter is static and every
/// caller in the assembly draws from it, so two rides in one square is not a thing that can happen.
/// </para>
/// </remarks>
internal sealed class ProxyPackageFleet : IAsyncDisposable
{
    public const string FareTokenKey = "mageride-c122-e2e-fare-estimate-key";
    public const string RideInternalKey = "mageride-c122-e2e-ride-internal-key";
    public const string DispatchInternalKey = "mageride-c122-e2e-dispatch-internal-key";
    public const string FareInternalKey = "mageride-c122-e2e-fare-internal-key";
    public const string ReputationInternalKey = "mageride-c122-e2e-reputation-key";
    public const string IamInternalKey = "mageride-c122-e2e-iam-internal-key";
    public const string ContentInternalKey = "mageride-c122-e2e-content-internal-key";
    public const string NotificationInternalKey = "mageride-c122-e2e-notification-internal-key";
    public const string SafetyInternalKey = "mageride-c122-e2e-safety-internal-key";

    public const string PhoneHashKey = "mageride-c122-e2e-rider-phone-hash-key";
    public const string OtpPepper = "mageride-c122-e2e-package-otp-pepper";

    /// <summary>
    /// What <c>{{link}}</c> is built on — the host D6' I-29.2 gives the no-login pages.
    /// </summary>
    /// <remarks>
    /// The real value, not a local address: the SMS is composed for a phone and not for this process,
    /// and <see cref="WebSubview"/> takes the *token* out of the link and presents it to the
    /// public-bff socket the way a browser resolving that host would arrive at the pod.
    /// </remarks>
    public const string WebTrackBaseUrl = "https://passenger.mageride.lk/track?token=";

    /// <summary>C120's window, held here for C120's reason.</summary>
    /// <remarks>
    /// D5' §3.5 gives 15 s. Widened so a package scenario that has to reach <c>Accepted</c> through a
    /// real offer is not racing the R-04 backstop on a loaded build host; the value is pinned in
    /// dispatch-svc's own suite (C034 <c>OfferExpiryTests</c>).
    /// </remarks>
    public static readonly TimeSpan OfferTtl = TimeSpan.FromSeconds(60);

    /// <summary>P-02 / AL-45's 300 s, at the spec value, because scenarios assert on it.</summary>
    public static readonly TimeSpan LocationRequestTtl = TimeSpan.FromSeconds(300);

    /// <summary>P-14 / D5' §8.3's 24 h, at the spec value, for the same reason.</summary>
    public static readonly TimeSpan CodUncollectedGrace = TimeSpan.FromHours(24);

    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private static ProxyPackageFleet? _shared;

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication[] _services;
    private readonly PostgresFixture _postgres;
    private readonly string _proofPhotoRoot;

    /// <summary>
    /// The four service sockets this fleet talks to over HTTP.
    /// </summary>
    /// <remarks>
    /// <b>Private, unlike C120's.</b> Every call C122 makes has a named method on this class —
    /// <c>RequestLocationAsync</c>, <c>PickupOtpAsync</c>, <c>CloseTripSharesAsync</c> — because the
    /// interesting thing about each one is *which surface* it is: the booker's, the driver's, the
    /// browser's, or a peer service's internal plane. A scenario reaching for a raw client would be
    /// making that choice silently.
    /// </remarks>
    private readonly HttpClient _ride;
    private readonly HttpClient _dispatch;
    private readonly HttpClient _fare;
    private readonly HttpClient _safety;

    private ProxyPackageFleet(
        WebApplication[] services,
        WebApplication ride,
        WebApplication dispatch,
        WebApplication fare,
        WebApplication fanout,
        WebApplication safety,
        WebApplication publicBff,
        TestTokenIssuer tokens,
        SmsGateway sms,
        PostgresFixture postgres,
        string proofPhotoRoot)
    {
        _services = services;
        _postgres = postgres;
        _proofPhotoRoot = proofPhotoRoot;

        Tokens = tokens;
        Sms = sms;

        _ride = NewClient(ride);
        _dispatch = NewClient(dispatch);
        _fare = NewClient(fare);
        _safety = NewClient(safety);
        FanoutBaseAddress = new Uri(BaseAddressOf(fanout));
        Web = new WebSubview(BaseAddressOf(publicBff));
        Journal = new RideJournal(postgres);
    }

    /// <summary>Where fanout-svc's <c>/hubs/live</c> is listening — P-13's booker socket.</summary>
    public Uri FanoutBaseAddress { get; }

    /// <summary>The six SCR-WT pages, as a browser reaches them.</summary>
    public WebSubview Web { get; }

    /// <summary>Fit SMS's stand-in — where the AL-45 and AL-21 links actually arrive.</summary>
    public SmsGateway Sms { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>Prints a ride's whole history when an assertion fails.</summary>
    public RideJournal Journal { get; }

    // -----------------------------------------------------------------------------------------
    // Lifetime
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The fleet, started at most once per test run.
    /// </summary>
    /// <remarks>
    /// Never disposed on purpose, for C120's reason: the ten services live as long as the test host,
    /// and tearing them down between classes would pay the consumer-group replay again. The
    /// containers are the TestKit's and the Testcontainers reaper removes them when the process ends;
    /// <see cref="DisposeAsync"/> exists for a caller that wants the sockets and the proof-photo
    /// directory back sooner.
    /// </remarks>
    public static async Task<ProxyPackageFleet> SharedAsync(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redpanda);

        if (_shared is not null)
        {
            return _shared;
        }

        await SharedGate.WaitAsync();

        try
        {
            return _shared ??= await StartAsync(postgres, redis, redpanda);
        }
        finally
        {
            SharedGate.Release();
        }
    }

    private static async Task<ProxyPackageFleet> StartAsync(
        PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    {
        postgres.RequireAvailable();
        redis.RequireAvailable();
        redpanda.RequireAvailable();

        await postgres.EnsureMigratedAsync();

        // Created up front rather than left to auto-creation: notification-svc subscribes to four
        // and a consumer that finds no topic spends its first scenario retrying metadata.
        foreach (var topic in new[]
                 {
                     "ride.events", "dispatch.events", "registry.events", "wallet.events",
                     "telemetry.normalized", "audit.events",
                 })
        {
            await redpanda.CreateTopicAsync(topic);
        }

        var tokens = new TestTokenIssuer();
        var sms = await SmsGateway.StartAsync();

        // D-36's filesystem fallback. `Storage__S3__Endpoint` is unset in this fleet, so P-10's
        // bytes land under a directory this run owns and deletes — which is what the kernel says
        // happens when there is no bucket, said out loud rather than discovered.
        var proofPhotoRoot = Path.Combine(Path.GetTempPath(), $"mageride-c122-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(proofPhotoRoot);

        // Start order is dependency order. iam-svc and content-svc answer other people's questions
        // and are first; ride-svc needs iam-svc's address; notification-svc needs content-svc's;
        // public-bff needs ride-svc's and safety-svc's.
        var iam = BuildIam(postgres, redis, tokens);
        await iam.StartAsync();

        var content = BuildContent(postgres, redis, tokens);
        await content.StartAsync();

        var ride = BuildRide(postgres, redpanda, tokens, BaseAddressOf(iam), proofPhotoRoot);
        await ride.StartAsync();

        var reputation = BuildReputation(postgres, redis, redpanda, tokens);
        await reputation.StartAsync();

        var dispatch = BuildDispatch(
            postgres, redis, redpanda, tokens, BaseAddressOf(ride), GrpcAddressOf(reputation));
        await dispatch.StartAsync();

        var fare = BuildFare(postgres, tokens, BaseAddressOf(ride), BaseAddressOf(dispatch));
        await fare.StartAsync();

        var fanout = BuildFanout(redis, redpanda, tokens);
        await fanout.StartAsync();

        var notification = BuildNotification(
            postgres, redis, redpanda, tokens, BaseAddressOf(content), sms.BaseAddress);
        await notification.StartAsync();

        var safety = BuildSafety(postgres, redis, tokens, BaseAddressOf(notification));
        await safety.StartAsync();

        var publicBff = BuildPublicBff(
            postgres, redis, tokens, BaseAddressOf(ride), BaseAddressOf(safety));
        await publicBff.StartAsync();

        // Reverse of the start order, for the disposal walk.
        WebApplication[] services =
        [
            publicBff, safety, notification, fanout, fare, dispatch, reputation, ride, content, iam,
        ];

        return new ProxyPackageFleet(
            services, ride, dispatch, fare, fanout, safety, publicBff, tokens, sms, postgres, proofPhotoRoot);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[] { _ride, _dispatch, _fare, _safety })
        {
            client.Dispose();
        }

        Web.Dispose();

        foreach (var app in _services)
        {
            await app.StopAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
        }

        await Sms.DisposeAsync();

        try
        {
            Directory.Delete(_proofPhotoRoot, recursive: true);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a run over.
        }
    }

    // -----------------------------------------------------------------------------------------
    // Seeding. Neither ride-svc nor dispatch-svc creates accounts or vehicles.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A passenger account, with the first name SCR-WT-003 renders and the number iam-svc's lookup
    /// finds.
    /// </summary>
    /// <remarks>
    /// <c>first_name</c> is seeded because <c>PickupConfirmSnapshotResponse.bookerFirstName</c> is
    /// the whole of what an unregistered rider is told about who is asking (P-02), and a fixture
    /// that left it null would make that assertion vacuous.
    /// </remarks>
    public async Task<Passenger> CreatePassengerAsync(string firstName = "Nimal")
    {
        var id = Guid.NewGuid();
        var phone = NextPhone();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role, first_name) VALUES (@Id, @Phone, 'passenger', @FirstName);",
            new { Id = id, Phone = phone, FirstName = firstName });

        return new Passenger(id, phone, Tokens.Passenger(id));
    }

    /// <summary>
    /// A number that belongs to nobody — P-03's unregistered rider and AL-21's unregistered
    /// recipient.
    /// </summary>
    /// <remarks>
    /// Nothing is written. That is the point: <c>iam.users</c> not having the row is the entire
    /// difference between the FCM branch and the SMS branch, and iam-svc is asked over HTTP rather
    /// than assumed.
    /// </remarks>
    public static string UnregisteredPhone() => NextPhone();

    /// <inheritdoc cref="ModeCFleet.CreateDriverAsync"/>
    public async Task<Driver> CreateDriverAsync(string vehicleType = "three_wheeler")
    {
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var plate = NextPlate();
        var phone = NextPhone();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name)
            VALUES (@DriverId, @Phone, 'driver', 'Sunil');

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

    public async Task<Driver> CreateOnlineDriverAsync(GeoPoint at, string vehicleType = "three_wheeler")
    {
        var driver = await CreateDriverAsync(vehicleType);

        using var response = await PostAsync(
            _dispatch,
            "/v1/standby/online",
            new
            {
                vehicleId = driver.VehicleId.ToString(),
                position = new { lat = at.Latitude, lng = at.Longitude },
            },
            driver.Bearer);

        await AssertOkAsync(response, $"driver {driver.DriverId} going on standby");

        return driver;
    }

    // -----------------------------------------------------------------------------------------
    // The proxy round-trip (§11.15, P-02, P-13)
    // -----------------------------------------------------------------------------------------

    /// <summary>The booker taps "Request location" — <c>POST /v1/location-requests</c>.</summary>
    public async Task<LocationRequest> RequestLocationAsync(Passenger booker, string riderPhone)
    {
        ArgumentNullException.ThrowIfNull(booker);

        using var response = await PostAsync(
            _ride, "/v1/location-requests", new { riderPhone }, booker.Bearer);

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            Assert.Fail(
                $"Issuing a location request answered {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync());
        }

        var body = await ReadJsonAsync(response);

        return new LocationRequest(
            body.GetProperty("requestId").GetGuid(),
            body.GetProperty("state").GetString()!,
            body.GetProperty("expiresAt").GetDateTimeOffset(),
            body.GetProperty("ttl").GetInt32());
    }

    /// <summary>Raw, so the P-12 refusal can be read as a status code.</summary>
    public Task<HttpResponseMessage> RequestLocationRawAsync(Passenger booker, string riderPhone)
    {
        ArgumentNullException.ThrowIfNull(booker);

        return PostAsync(_ride, "/v1/location-requests", new { riderPhone }, booker.Bearer);
    }

    /// <summary>The registered rider taps Share, in the app (§11.15's <c>IF Share + Confirm</c>).</summary>
    public Task<HttpResponseMessage> ConfirmLocationAsync(
        Passenger rider, Guid requestId, GeoPoint at, double? accuracy = 12) =>
        PostAsync(
            _ride,
            $"/v1/location-requests/{requestId}/confirm",
            new { lat = at.Latitude, lng = at.Longitude, accuracy },
            rider?.Bearer);

    /// <summary>The registered rider taps Decline. No body — there is no parameter for a coordinate.</summary>
    public Task<HttpResponseMessage> DeclineLocationAsync(Passenger rider, Guid requestId) =>
        PostAsync(_ride, $"/v1/location-requests/{requestId}/decline", null, rider?.Bearer);

    /// <summary>
    /// A party's own read of the request (D3' <c>GET /v1/location-requests/{id}</c>) — the recovery
    /// path when the socket dropped mid-round-trip.
    /// </summary>
    public async Task<JsonElement> ReadLocationRequestAsync(Passenger caller, Guid requestId)
    {
        using var response = await ReadLocationRequestRawAsync(caller, requestId);
        await AssertOkAsync(response, $"reading location request {requestId}");

        return await ReadJsonAsync(response);
    }

    /// <summary>Raw, so the refusal a non-party gets can be read as a status code.</summary>
    public Task<HttpResponseMessage> ReadLocationRequestRawAsync(Passenger caller, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/location-requests/{requestId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Bearer);

        return _ride.SendAsync(request);
    }

    /// <summary>The <c>rides.location_requests</c> row, as a scenario reads it back.</summary>
    public async Task<LocationRequestSnapshot> ReadLocationRowAsync(Guid requestId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<LocationRequestSnapshot>(
            """
            SELECT id AS Id, request_id AS RequestId, booker_id AS BookerId, rider_id AS RiderId,
                   state AS State, issued_at AS IssuedAt, ttl_seconds AS TtlSeconds,
                   resolved_at AS ResolvedAt,
                   ST_Y(resolved_geo::geometry) AS ResolvedLat,
                   ST_X(resolved_geo::geometry) AS ResolvedLng,
                   resolved_accuracy_m AS ResolvedAccuracyM
              FROM rides.location_requests WHERE request_id = @RequestId;
            """,
            new { RequestId = requestId });
    }

    /// <summary>
    /// Brings a location request's 300 s deadline into the past so ride-svc's own sweep expires it.
    /// </summary>
    /// <remarks>
    /// <b>A clock, not a state fix</b> — C120's rule, and the reason it is allowed. The column moved
    /// is <c>issued_at</c>, the platform's own record of *when* the booker asked; the state, the
    /// resolution and the geo are untouched. Every caller asserts <see cref="LocationRequestTtl"/> off
    /// the row first (<see cref="AssertLocationRequestWindowAsync"/>), so what is being tested is
    /// ADD §11.15's window and then its sweep, rather than the sweep alone. The alternative is a
    /// scenario that takes five minutes.
    /// </remarks>
    public async Task AgeLocationRequestAsync(Guid requestId)
    {
        await AssertLocationRequestWindowAsync(requestId);

        await using var connection = await OpenAsync();

        // Both live states, because the platform's own sweep claims both: `RiderNotRegistered` runs
        // down the same 300 s clock a `Pending` one does — AL-45 makes it an answerable request
        // rather than a terminal, and `ClaimExpiredAsync`'s predicate says so.
        var moved = await connection.ExecuteAsync(
            """
            UPDATE rides.location_requests
               SET issued_at = issued_at - make_interval(secs => ttl_seconds + 5)
             WHERE request_id = @RequestId AND state IN ('Pending', 'RiderNotRegistered');
            """,
            new { RequestId = requestId });

        Assert.True(
            moved > 0,
            $"Location request {requestId} is already resolved, so it has no deadline to bring forward.");
    }

    /// <summary>Asserts the platform armed P-02's window, before anything moves it.</summary>
    public async Task AssertLocationRequestWindowAsync(Guid requestId)
    {
        var row = await ReadLocationRowAsync(requestId);

        Assert.Equal((int)LocationRequestTtl.TotalSeconds, row.TtlSeconds);
    }

    // -----------------------------------------------------------------------------------------
    // The ride, through the surfaces an app has
    // -----------------------------------------------------------------------------------------

    public async Task<(long AmountMinor, string Token)> QuoteAsync(
        Passenger passenger, GeoPoint from, GeoPoint to, string vehicleType, string kind)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"/v1/fare/estimate?fromLat={from.Latitude}&fromLng={from.Longitude}" +
            $"&toLat={to.Latitude}&toLng={to.Longitude}&vehicleType={vehicleType}&kind={kind}");

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", passenger.Bearer);

        using var response = await _fare.SendAsync(request);
        await AssertOkAsync(response, "fare estimate");

        var body = await ReadJsonAsync(response);

        return (body.GetProperty("amountMinor").GetInt64(), body.GetProperty("fareEstimateToken").GetString()!);
    }

    /// <summary>
    /// Books a proxy ride — a booker arranging a ride for somebody else (P-01).
    /// </summary>
    /// <param name="riderId">
    /// The rider's account when they have one. <see langword="null"/> is P-03's unregistered rider,
    /// whose number ride-svc keeps only as a digest.
    /// </param>
    public Task<LiveRide> BookProxyAsync(
        Passenger booker,
        Driver driver,
        GeoPoint pickup,
        GeoPoint dropoff,
        string riderPhone,
        string riderName = "Kamala",
        string paymentMethod = "cash") =>
        BookAsync(
            booker,
            driver,
            pickup,
            dropoff,
            new
            {
                kind = "proxy",
                isProxy = true,
                riderName,
                riderPhone,
                paymentMethod,
            });

    /// <summary>Books a package delivery (P-06).</summary>
    public Task<LiveRide> BookPackageAsync(
        Passenger sender,
        Driver driver,
        GeoPoint pickup,
        GeoPoint dropoff,
        string recipientPhone,
        string packageSize = "S",
        string recipientName = "Kamala",
        string paymentMethod = "cash") =>
        BookAsync(
            sender,
            driver,
            pickup,
            dropoff,
            new
            {
                kind = "package",
                packageSize,
                packageDescription = "One box of mangoes",
                recipientName,
                recipientPhone,
                paymentMethod,
            });

    private async Task<LiveRide> BookAsync(
        Passenger passenger, Driver driver, GeoPoint pickup, GeoPoint dropoff, object kindSpecific)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        var extra = JsonSerializer.SerializeToElement(kindSpecific);
        var kind = extra.GetProperty("kind").GetString()!;
        var vehicleType = "three_wheeler";

        // **fare-svc's quote vocabulary is two-valued where ride-svc's `kind` is three-valued**, and
        // that is correct rather than a mismatch: D5' §10 gives a proxy booking the existing tariff
        // untouched — what proxy changes is the *payer* (P-04), not the price — so a proxy ride is
        // quoted as the passenger ride it is. `FareEstimator` refuses anything else by name.
        var quoteKind = kind is "package" ? "package" : "passenger";

        var (_, token) = await QuoteAsync(passenger, pickup, dropoff, vehicleType, quoteKind);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["clientRequestId"] = Guid.NewGuid().ToString(),
            ["pickup"] = new { lat = pickup.Latitude, lng = pickup.Longitude, address = "E2E pickup" },
            ["dropoff"] = new { lat = dropoff.Latitude, lng = dropoff.Longitude, address = "E2E dropoff" },
            ["vehicleType"] = vehicleType,
            ["fareEstimateToken"] = token,
        };

        foreach (var member in extra.EnumerateObject())
        {
            body[member.Name] = member.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => member.Value.GetString(),
            };
        }

        using var response = await PostAsync(_ride, "/v1/rides/request", body, passenger.Bearer);

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            Assert.Fail(
                $"Booking a {kind} ride answered {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync());
        }

        var booked = await ReadJsonAsync(response);

        return new LiveRide(
            booked.GetProperty("rideId").GetGuid(),
            passenger,
            driver,
            booked.GetProperty("version").GetInt64(),
            pickup,
            dropoff)
        {
            PickupOtp = booked.TryGetProperty("pickupOtp", out var otp) && otp.ValueKind is JsonValueKind.String
                ? otp.GetString()
                : null,
        };
    }

    public Task<HttpResponseMessage> AcceptAsync(Guid rideId, Driver driver, Guid offerId, long version)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return PostAsync(
            _ride,
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offerId.ToString(), version },
            driver.Bearer);
    }

    /// <summary>One driver-side move — <c>arrive</c>, <c>start</c> or <c>complete</c>.</summary>
    public async Task<LiveRide> AdvanceAsync(LiveRide ride, string command)
    {
        ArgumentNullException.ThrowIfNull(ride);

        using var response = await PostAsync(
            _ride, $"/v1/rides/{ride.RideId}/{command}", new { version = ride.Version }, ride.Driver.Bearer);

        await AssertOkAsync(response, $"{command} on ride {ride.RideId}");

        return ride with { Version = (await ReadJsonAsync(response)).GetProperty("version").GetInt64() };
    }

    /// <summary>The driver types four digits at the sender's door (P-07, SCR-DA/DI-016b).</summary>
    public Task<HttpResponseMessage> PickupOtpAsync(LiveRide ride, string? otp)
    {
        ArgumentNullException.ThrowIfNull(ride);

        return PostAsync(
            _ride, $"/v1/rides/{ride.RideId}/package/pickup-otp", new { otp }, ride.Driver.Bearer);
    }

    /// <summary>The driver types four digits at the recipient's door (P-07, SCR-DA/DI-016c).</summary>
    public Task<HttpResponseMessage> DeliveryOtpAsync(LiveRide ride, string? otp)
    {
        ArgumentNullException.ThrowIfNull(ride);

        return PostAsync(
            _ride, $"/v1/rides/{ride.RideId}/package/delivery-otp", new { otp }, ride.Driver.Bearer);
    }

    /// <summary>
    /// The driver photographs the parcel because nobody answered (P-10).
    /// </summary>
    /// <remarks>
    /// Multipart, as the route takes it — the bytes cross a real HTTP boundary into the kernel's
    /// <c>IObjectStore</c>, which with <c>Storage__S3__Endpoint</c> unset is D-36's documented
    /// filesystem fallback under a directory this fleet owns.
    /// </remarks>
    public async Task<HttpResponseMessage> ProofPhotoAsync(LiveRide ride, GeoPoint? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(ride);

        using var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(JpegBytes());
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(bytes, "file", "doorstep.jpg");

        // The optional parts behind `rides.proof_artifacts.captured_geo`. A driver whose GPS is not
        // fixed still gets to complete the delivery, so these are sent the way the app sends them —
        // when there is a position to send.
        if (capturedAt is { } at)
        {
            content.Add(new StringContent(at.Latitude.ToString(CultureInfo.InvariantCulture)), "lat");
            content.Add(new StringContent(at.Longitude.ToString(CultureInfo.InvariantCulture)), "lng");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/rides/{ride.RideId}/package/proof-photo")
        {
            Content = content,
        };

        request.Headers.Add(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ride.Driver.Bearer);

        return await _ride.SendAsync(request);
    }

    /// <summary>The driver taps "Cash received" (P-08, AL-33's third sheet).</summary>
    public Task<HttpResponseMessage> CodCollectedAsync(LiveRide ride, long collectedMinor)
    {
        ArgumentNullException.ThrowIfNull(ride);

        return PostAsync(
            _ride,
            $"/v1/rides/{ride.RideId}/cod-collected",
            new { collectedMinor },
            ride.Driver.Bearer);
    }

    /// <summary>The driver's own read of the ride — where P-05 is decided (<c>counterpartyPhone</c>).</summary>
    public async Task<(HttpStatusCode Status, string Body)> ReadRideAsAsync(Guid rideId, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/rides/{rideId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await _ride.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // -----------------------------------------------------------------------------------------
    // safety-svc's two doors onto the same token table
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The trip-end hook that closes every share on a ride — SCR-WT-006's cause (AL-44).
    /// </summary>
    /// <remarks>
    /// safety-svc's, not this fleet's and not public-bff's: the window is a fact about the trip
    /// rather than about who is looking at it, so the revocation belongs to the service that owns
    /// the table. Called here as its internal caller would call it, which is the only way to reach
    /// the expired page without writing to <c>safety.trip_share_tokens</c> by hand.
    /// </remarks>
    public Task<HttpResponseMessage> CloseTripSharesAsync(Guid tripId) =>
        PostInternalAsync(
            _safety, $"/v1/internal/safety/trips/{tripId}/close", new { }, SafetyInternalKey);

    /// <summary>
    /// D-34's own share link, which <c>/public/track</c> refuses (<c>POST /v1/trip-share/{tripId}</c>).
    /// </summary>
    /// <remarks>
    /// A different contract with a different redaction, served by safety-svc's own public view.
    /// Minted through the real route a passenger uses so the refusal is asserted against a token
    /// that is genuinely live somewhere else, rather than against a string nobody issued.
    /// </remarks>
    public async Task<string> ShareTripAsync(Passenger passenger, Guid rideId)
    {
        ArgumentNullException.ThrowIfNull(passenger);

        using var response = await PostAsync(
            _safety, $"/v1/trip-share/{rideId}", new { }, passenger.Bearer);

        if (response.StatusCode != HttpStatusCode.Created)
        {
            Assert.Fail(
                $"Sharing ride {rideId} answered {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync());
        }

        return (await ReadJsonAsync(response)).GetProperty("token").GetString()!;
    }

    /// <inheritdoc cref="ModeCFleet.PriceAsync"/>
    public async Task<Guid> PriceAsync(Guid rideId)
    {
        using var response = await PostInternalAsync(
            _fare, "/v1/fare/calculate", new { rideId = rideId.ToString() }, FareInternalKey);

        await AssertOkAsync(response, $"pricing ride {rideId}");

        return (await ReadJsonAsync(response)).GetProperty("paymentId").GetGuid();
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

    /// <summary>The event types a ride or a request put on <c>rides.outbox</c>, oldest first.</summary>
    public async Task<IReadOnlyList<string>> ReadEventsAsync(Guid aggregateId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<string>(
            "SELECT event_type FROM rides.outbox WHERE aggregate_id = @AggregateId ORDER BY id;",
            new { AggregateId = aggregateId })];
    }

    /// <summary>One <c>rides.outbox</c> payload, as JSON.</summary>
    public async Task<JsonElement> ReadEventPayloadAsync(Guid aggregateId, string eventType)
    {
        await using var connection = await OpenAsync();

        var payload = await connection.QuerySingleOrDefaultAsync<string>(
            """
            SELECT payload::text FROM rides.outbox
             WHERE aggregate_id = @AggregateId AND event_type = @EventType ORDER BY id DESC LIMIT 1;
            """,
            new { AggregateId = aggregateId, EventType = eventType });

        Assert.True(payload is not null, $"{aggregateId} never raised '{eventType}'.");

        using var document = JsonDocument.Parse(payload!);
        return document.RootElement.Clone();
    }

    /// <summary>Every <c>rides.outbox</c> payload of one type, as raw JSON text.</summary>
    public async Task<IReadOnlyList<string>> ReadEventPayloadsAsync(Guid aggregateId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<string>(
            "SELECT payload::text FROM rides.outbox WHERE aggregate_id = @AggregateId ORDER BY id;",
            new { AggregateId = aggregateId })];
    }

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

    /// <summary>The <c>fares.ride_payments</c> chain for a ride, newest attempt last (D-10).</summary>
    public async Task<IReadOnlyList<PaymentSnapshot>> ReadPaymentsAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<PaymentSnapshot>(
            """
            SELECT id AS Id, state AS State, method AS Method,
                   amount_minor::bigint AS AmountMinor, payer_role AS PayerRole,
                   payer_user_id AS PayerUserId
              FROM fares.ride_payments WHERE ride_id = @RideId ORDER BY created_at, id;
            """,
            new { RideId = rideId })];
    }

    /// <summary>The P-10 evidence a delivery left behind.</summary>
    public async Task<IReadOnlyList<(string Kind, string StorageUrl)>> ReadProofArtifactsAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<(string, string)>(
            "SELECT kind, storage_url FROM rides.proof_artifacts WHERE ride_id = @RideId ORDER BY captured_at;",
            new { RideId = rideId })];
    }

    /// <summary>The P-12 abuse ledger, oldest first (<c>safety.location_request_audit</c>).</summary>
    public async Task<IReadOnlyList<string>> ReadLocationAuditAsync(Guid bookerId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<string>(
            """
            SELECT decision FROM safety.location_request_audit
             WHERE booker_id = @BookerId ORDER BY ts, id;
            """,
            new { BookerId = bookerId })];
    }

    /// <summary>The share tokens notification-svc minted for one trip or request.</summary>
    public async Task<IReadOnlyList<ShareTokenSnapshot>> ReadShareTokensAsync(Guid? tripId, Guid? locationRequestId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<ShareTokenSnapshot>(
            """
            SELECT token AS Token, scope AS Scope, expires_at AS ExpiresAt, revoked_at AS RevokedAt,
                   access_count AS AccessCount, last_access_at AS LastAccessAt
              FROM safety.trip_share_tokens
             WHERE (@TripId::uuid IS NOT NULL AND trip_id = @TripId)
                OR (@LocationRequestId::uuid IS NOT NULL AND location_request_id = @LocationRequestId)
             ORDER BY created_at;
            """,
            new { TripId = tripId, LocationRequestId = locationRequestId })];
    }

    /// <summary>The <c>safety.sos_events</c> rows a web panic button produced (US-25.5).</summary>
    public async Task<IReadOnlyList<WebSosSnapshot>> ReadWebSosAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<WebSosSnapshot>(
            """
            SELECT id AS Id, user_id AS UserId, source AS Source, lat AS Lat, lng AS Lng,
                   share_token AS ShareToken, sms_status AS SmsStatus
              FROM safety.sos_events WHERE ride_id = @RideId ORDER BY ts;
            """,
            new { RideId = rideId })];
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    // -----------------------------------------------------------------------------------------
    // Waiting
    // -----------------------------------------------------------------------------------------

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

    public async Task<OfferSnapshot> WaitForOfferAsync(Guid rideId, TimeSpan? within = null)
    {
        await WaitForStateAsync(rideId, "Offered", within);

        var offer = await ReadOfferAsync(rideId);

        Assert.True(
            offer is not null,
            $"Ride {rideId} reached Offered with no dispatch.offers row." + await Journal.DescribeAsync(rideId));

        return offer!;
    }

    /// <summary>Waits for the location request to reach <paramref name="state"/>.</summary>
    public async Task<LocationRequestSnapshot> WaitForRequestStateAsync(
        Guid requestId, string state, TimeSpan? within = null)
    {
        var timeout = within ?? TimeSpan.FromSeconds(30);
        var deadline = DateTimeOffset.UtcNow + timeout;

        LocationRequestSnapshot snapshot;

        do
        {
            snapshot = await ReadLocationRowAsync(requestId);

            if (string.Equals(snapshot.State, state, StringComparison.Ordinal))
            {
                return snapshot;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail(
            $"Location request {requestId} was still {snapshot.State} after {timeout.TotalSeconds:F0}s "
            + $"waiting for {state}.");

        return snapshot;
    }

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

    /// <inheritdoc cref="ModeCFleet.PullForwardRideTimerAsync"/>
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

    /// <inheritdoc cref="ModeCFleet.AssertTimerArmedAsync"/>
    public async Task AssertTimerArmedAsync(Guid rideId, string kind, TimeSpan window)
    {
        var timers = await ReadRideTimersAsync(rideId, kind);

        Assert.True(
            timers.Count == 1,
            $"Ride {rideId} carries {timers.Count} live '{kind}' timers, not one."
            + await Journal.DescribeAsync(rideId));

        var armed = timers[0].FireAt - DateTimeOffset.UtcNow;
        var slack = TimeSpan.FromSeconds(Math.Max(5, window.TotalSeconds * 0.2));

        Assert.True(
            (armed - window).Duration() < slack,
            $"The '{kind}' timer on ride {rideId} is due in {armed}, not the {window} the spec gives."
            + await Journal.DescribeAsync(rideId));
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

    /// <summary>The smallest thing a JPEG sniffer will accept, for P-10's upload.</summary>
    private static byte[] JpegBytes()
    {
        var bytes = new byte[1024];

        // SOI + APP0/JFIF, then EOI at the end. The route checks the magic, not the picture.
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;

        return bytes;
    }

    // -----------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------

    private static WebApplication BuildIam(
        PostgresFixture postgres, RedisFixture redis, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",

                // The one route this fleet calls, and the key that maps it at all: unset, ride-svc's
                // lookup is a 404 and every proxy booking answers 503.
                ["Auth:InternalApiKey"] = IamInternalKey,
                ["Auth:PhoneHashKey"] = "mageride-c122-e2e-iam-phone-hash-key",

                // The floor `AuthPolicyOptions` enforces. Nothing here hashes a password — this
                // fleet's only iam-svc route is the P-03 lookup — but the option is validated at
                // start-up whether or not it is used.
                ["Auth:PasswordIterations"] = "100000",
                ["Otp:PepperKey"] = "mageride-c122-e2e-otp-pepper",
                ["Mqtt:SessionTokenSecret"] = "mageride-c122-e2e-mqtt-session-secret",
            },
            (options, configure) => IamApplication.Build(options, configure));

    private static WebApplication BuildContent(
        PostgresFixture postgres, RedisFixture redis, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Content:InternalApiKey"] = ContentInternalKey,
            },
            (options, configure) => ContentApplication.Build(options, configure));

    private static WebApplication BuildRide(
        PostgresFixture postgres,
        RedpandaFixture redpanda,
        TestTokenIssuer tokens,
        string iamBaseUrl,
        string proofPhotoRoot) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Fare:EstimateTokenKey"] = FareTokenKey,
                ["Ride:InternalApiKey"] = RideInternalKey,
                ["Ride:PhoneHashKey"] = PhoneHashKey,
                ["Ride:OtpPepper"] = OtpPepper,
                ["Ride:OfferTtl"] = OfferTtl.ToString(),
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",

                // On, and this fleet leans on it twice: the R-04 sweep is also the pass that closes
                // an unanswered location request (`ExpireDueAsync` rides in `RideTimerWorker`), and
                // it is what fires P-14's `cod_uncollected`.
                ["Ride:TimersEnabled"] = "true",

                // P-03's oracle. Unset, the whole `/v1/location-requests` family is 503.
                ["Ride:IamBaseUrl"] = iamBaseUrl,
                ["Ride:IamInternalApiKey"] = IamInternalKey,

                // The spec values, because scenarios assert on both before moving either clock.
                ["Ride:LocationRequestTtl"] = LocationRequestTtl.ToString(),
                ["Ride:CodUncollectedGrace"] = CodUncollectedGrace.ToString(),

                // D-36's documented filesystem fallback: `Storage__S3__Endpoint` is unset in this
                // fleet, so P-10's bytes land here rather than in a bucket that does not exist.
                ["Ride:ProofPhotoRoot"] = proofPhotoRoot,

                // Off for C120's reason: three suites share one database and each would otherwise
                // gauge the others' rides.
                ["Ride:StuckStateMetricsEnabled"] = "false",

                // Off: no broker in this collection, and nothing on the proxy or package path is
                // downstream of R-15's last will.
                ["Ride:VehicleStatusEnabled"] = "false",
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
                ["Reputation:GrpcListenPort"] = "0",
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
                ["Dispatch:ConsumerGroup"] = $"dispatch-svc-c122-{Guid.NewGuid():N}",
                ["Dispatch:ConsumerEnabled"] = "true",
                ["Dispatch:PositionConsumerEnabled"] = "true",
                ["Dispatch:ExpiryWorkerEnabled"] = "true",
                ["Dispatch:DispatchTimerWorkerEnabled"] = "true",
                ["Dispatch:KeyspaceNotificationsEnabled"] = "true",
                ["Dispatch:ReputationCacheTtl"] = "00:00:00",

                // Off: no broker in this collection.
                ["Dispatch:LastWillEnabled"] = "false",
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
                ["Fare:RideBaseUrl"] = rideBaseUrl,
                ["Fare:RideInternalApiKey"] = RideInternalKey,
                ["Fare:DispatchBaseUrl"] = dispatchBaseUrl,
                ["Fare:DispatchInternalApiKey"] = DispatchInternalKey,
                ["Fare:QrNudgeEnabled"] = "false",
            },
            (options, configure) => FareApplication.Build(options, configure));

    private static WebApplication BuildFanout(
        RedisFixture redis, RedpandaFixture redpanda, TestTokenIssuer tokens) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Fanout:ConsumerGroup"] = $"fanout-svc-c122-{Guid.NewGuid():N}",
                ["Fanout:EventsEnabled"] = "true",

                // P-13's plane: `location.request.*` reaches the booker's socket through the control
                // plane, not through the ride projection.
                ["Fanout:ControlPlaneEnabled"] = "true",

                // Off: no broker, no positions, nothing to pump.
                ["Fanout:PresenceEnabled"] = "false",
                ["Fanout:PumpEnabled"] = "false",
            },
            (options, configure) => FanoutApplication.Build(options, configure));

    private static WebApplication BuildNotification(
        PostgresFixture postgres,
        RedisFixture redis,
        RedpandaFixture redpanda,
        TestTokenIssuer tokens,
        string contentBaseUrl,
        string smsBaseUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Notification:InternalApiKey"] = NotificationInternalKey,

                // D-26: unset, nothing with a body is ever sent — no link, so no SCR-WT-002 and no
                // SCR-WT-003. The templates come from the real content-svc over HTTP, rendered from
                // what migrations 1902/1904 seeded.
                ["Notification:ContentBaseUrl"] = contentBaseUrl,
                ["Notification:ContentInternalApiKey"] = ContentInternalKey,

                // AL-44's `{{link}}`. The host is the real one; the browser is `WebSubview`.
                ["Notification:WebTrackBaseUrl"] = WebTrackBaseUrl,

                // AL-45's 300 s, at the spec value: the token may not outlive the request.
                ["Notification:PickupConfirmTokenTtl"] = LocationRequestTtl.ToString(),

                // The consumers and the queue, on. This is what turns a committed ride-svc
                // transaction into a message on somebody's phone with nothing in between called by
                // this assembly.
                //
                // **E-01's offer SMS fallback is left on too**, though it is notification-svc's own
                // subject and puts a message on the gateway for every dispatch this fleet performs.
                // Every assertion here is addressed to a recipient, so a driver's offer SMS cannot
                // be mistaken for a rider's link — and switching a real path off to keep a fixture
                // tidy is how a suite stops describing the platform.
                ["Notification:ConsumersEnabled"] = "true",
                ["Notification:ConsumerGroup"] = $"notification-svc-c122-{Guid.NewGuid():N}",
                ["Notification:DeliveryEnabled"] = "true",
                ["Notification:DeliveryInterval"] = "00:00:00.250",

                // Off: the queue's PDPA housekeeping is a 30-day sweep with nothing to find in a
                // run that lasts a minute, and it is E-06's subject rather than this suite's.
                ["Notification:RetentionSweepEnabled"] = "false",

                // The gateway is a third party, not a component — see SmsGateway.
                ["Sms:Provider"] = "fitsms",
                ["Sms:FitSmsBaseUrl"] = smsBaseUrl,
                ["Sms:FitSmsApiToken"] = "c122-e2e-key",
            },
            (options, configure) => NotificationApplication.Build(options, configure));

    private static WebApplication BuildSafety(
        PostgresFixture postgres, RedisFixture redis, TestTokenIssuer tokens, string notificationBaseUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",
                ["Kafka:BootstrapServers"] = "127.0.0.1:1",
                ["Outbox:DispatcherEnabled"] = "false",
                ["Safety:InternalApiKey"] = SafetyInternalKey,

                // D-33's dual-gateway SMS goes out through the real notification-svc beside it, and
                // lands on the same gateway everything else does.
                ["Safety:NotificationBaseUrl"] = notificationBaseUrl,
                ["Safety:NotificationInternalApiKey"] = NotificationInternalKey,

                // Off: the report path is C052's subject and reaches reputation-svc over a plane
                // this fleet has no scenario for.
                ["Safety:ReputationReportingEnabled"] = "false",
            },
            (options, configure) => SafetyApplication.Build(options, configure));

    private static WebApplication BuildPublicBff(
        PostgresFixture postgres,
        RedisFixture redis,
        TestTokenIssuer tokens,
        string rideBaseUrl,
        string safetyBaseUrl) =>
        Build(
            tokens,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Postgres:PgBouncerTransactionMode"] = "false",

                // AL-45's seam. Unset, SCR-WT-003's Share and Decline answer 503 and an unregistered
                // rider cannot answer at all.
                ["PublicBff:Ride:BaseUrl"] = rideBaseUrl,
                ["PublicBff:Ride:InternalApiKey"] = RideInternalKey,

                // US-25.5's seam. Unset, the web SOS answers 503 and nobody is told.
                ["PublicBff:Safety:BaseUrl"] = safetyBaseUrl,
                ["PublicBff:Safety:InternalApiKey"] = SafetyInternalKey,
            },
            (options, configure) => PublicBffApplication.Build(options, configure));

    /// <summary>
    /// The four things every service in this fleet is configured with, and the one thing every one
    /// of them has replaced: the bearer handler's signing key.
    /// </summary>
    /// <remarks>
    /// public-bff registers no authentication scheme at all — that is its first fence — so the
    /// <c>PostConfigure</c> below simply never runs there, which is the correct outcome rather than
    /// an exception to it.
    /// </remarks>
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
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(settings);
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });
    }

    private static HttpClient NewClient(WebApplication app) =>
        new() { BaseAddress = new Uri(BaseAddressOf(app)), Timeout = TimeSpan.FromSeconds(60) };

    private static string BaseAddressOf(WebApplication app) => AddressesOf(app)[0];

    /// <inheritdoc cref="ModeCFleet"/>
    private static string GrpcAddressOf(WebApplication app) => AddressesOf(app)[^1];

    private static IReadOnlyList<string> AddressesOf(WebApplication app) =>
        [.. app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses];

    private static string NextPlate() =>
        "WP-E3-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private static string NextPhone() =>
        "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);
}
