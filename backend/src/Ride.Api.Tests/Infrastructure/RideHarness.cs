using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Ride.Endpoints;
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

namespace MageRide.Ride.Tests.Infrastructure;

/// <summary>
/// A running ride-svc on a real socket, against a real Postgres.
/// </summary>
/// <remarks>
/// Built through <see cref="RideApplication.Build"/>, so the pipeline under test — deny-by-default
/// authorization, the idempotency middleware, the problem+json handler, the outbox writer — is the
/// one the process runs. Kestrel rather than TestServer for the same reason C008's, C020's and
/// C021's harnesses use it: the idempotency middleware swaps the response body feature.
/// </remarks>
internal sealed class RideHarness : IAsyncDisposable
{
    /// <summary>Long enough to satisfy <c>Fare:EstimateTokenKey</c>'s minimum length.</summary>
    public const string FareTokenKey = "mageride-c022-test-fare-estimate-key";

    /// <summary>Guards <c>/v1/internal/rides/**</c> until C042 lands a mesh.</summary>
    public const string InternalApiKey = "mageride-c022-test-internal-key";

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    /// <summary>The P-03 pepper and hash key. Long enough that nothing rejects them for length.</summary>
    public const string OtpPepper = "mageride-c037-test-package-otp-pepper";

    public const string PhoneHashKey = "mageride-c037-test-rider-phone-hash-key";

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;
    private readonly IamLookupStub _iam;
    private readonly string _proofPhotoRoot;

    private RideHarness(
        WebApplication app,
        HttpClient client,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        IamLookupStub iam,
        string proofPhotoRoot)
    {
        _app = app;
        _postgres = postgres;
        _iam = iam;
        _proofPhotoRoot = proofPhotoRoot;
        Client = client;
        Tokens = tokens;
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>iam-svc's registration oracle (P-03), on its own socket.</summary>
    public IamLookupStub Iam => _iam;

    /// <summary>Where P-10 delivery photos land for this harness.</summary>
    public string ProofPhotoRoot => _proofPhotoRoot;

    public IServiceProvider Services => _app.Services;

    /// <summary>The codec ride-svc verifies with — the same key fare-svc would sign with.</summary>
    public FareEstimateTokenCodec FareTokens => Services.GetRequiredService<FareEstimateTokenCodec>();

    /// <summary>Colombo Fort and Dehiwala: ~9 km apart, both inside the fare stub's service box.</summary>
    public static readonly GeoPoint Pickup = new(6.9344, 79.8428);
    public static readonly GeoPoint Dropoff = new(6.8514, 79.8653);

    public static async Task<RideHarness> StartAsync(
        PostgresFixture postgres, IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var tokens = new TestTokenIssuer();
        var iam = await IamLookupStub.StartAsync(postgres);

        // One directory per harness, so two concurrently running tests cannot read each other's
        // delivery photos and a failed run leaves its evidence behind to look at.
        var proofPhotoRoot = Path.Combine(
            Path.GetTempPath(), "mageride-c037", Guid.NewGuid().ToString("N"));

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ride:IamBaseUrl"] = iam.BaseAddress,
            ["Ride:OtpPepper"] = OtpPepper,
            ["Ride:PhoneHashKey"] = PhoneHashKey,
            ["Ride:ProofPhotoRoot"] = proofPhotoRoot,
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer — so the pooled DSN also serves the
            // LISTEN the outbox dispatcher registers (E-09).
            ["Postgres:PgBouncerTransactionMode"] = "false",
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's
            // auth wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Fare:EstimateTokenKey"] = FareTokenKey,
            ["Ride:InternalApiKey"] = InternalApiKey,
            // Bound and validated at start-up even when nothing produces; the outbox test replaces
            // it with a real broker.
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            // One /metrics endpoint per harness would collide across concurrently running tests.
            ["Otel:PrometheusEnabled"] = "false",
            // The R-04 sweep is driven one pass at a time by RideTimerTests rather than left
            // ticking: a background worker firing a no-show mid-assertion is the kind of flake that
            // is impossible to read afterwards.
            ["Ride:TimersEnabled"] = "false",
            // A dozen harnesses share one database, so each would otherwise gauge the others' rides.
            ["Ride:StuckStateMetricsEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = RideApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(overrides);
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                // PostConfigure so this runs after the kernel's AddMageRideAuth has built the
                // options. Everything else about validation — RS256 only, lifetime, issuer — is
                // left exactly as the kernel configured it, because that is what is under test.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        var baseAddress = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };

        return new RideHarness(app, client, tokens, postgres, iam, proofPhotoRoot);
    }

    // -------------------------------------------------------------------------------------------
    // Seeding. ride-svc creates neither accounts nor vehicles — iam-svc and registry-svc do.
    // -------------------------------------------------------------------------------------------

    /// <summary>Creates the <c>iam.users</c> row the ride's foreign keys need.</summary>
    public async Task<Guid> CreatePassengerAsync() => (await CreateUserAsync()).Id;

    /// <summary>
    /// The same account, with the number it is reachable on — which is what the P-03 lookup and
    /// AL-48's <c>counterpartyPhone</c> both turn on.
    /// </summary>
    public async Task<SeededUser> CreateUserAsync(string role = "passenger")
    {
        var userId = Guid.NewGuid();
        var phone = NextPhone();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, @Role);",
            new { Id = userId, Phone = phone, Role = role });

        return new SeededUser(userId, phone, Tokens.Passenger(userId));
    }

    /// <summary>
    /// Creates a driver with one APPROVED Mode C vehicle — what registry-svc's
    /// <c>POST /v1/vehicles</c> plus the dev approve path produce (C021).
    /// </summary>
    public async Task<SeededDriver> CreateDriverAsync(string vehicleType = "three_wheeler", string? driverName = null)
    {
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var name = driverName ?? "Test Driver";
        var plate = NextPlate();
        var phone = NextPhone();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role) VALUES (@DriverId, @Phone, 'driver');
            INSERT INTO registry.vehicles (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@VehicleId, @DriverId, @Plate, @VehicleType, 'C', 'APPROVED', @DriverName);
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                Phone = phone,
                Plate = plate,
                VehicleType = vehicleType,
                DriverName = name,
            });

        return new SeededDriver(driverId, vehicleId, plate, name, phone, Tokens.Driver(driverId));
    }

    // -------------------------------------------------------------------------------------------
    // HTTP
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// POSTs JSON with an <c>Idempotency-Key</c>. A fresh key unless the caller supplies one —
    /// D3' §0 makes the header mandatory, so omitting it by accident would test the 400 path.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? bearer, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        Authorize(request, bearer);

        return Client.SendAsync(request);
    }

    /// <summary>
    /// POSTs to <c>/v1/internal/**</c> with the shared secret dispatch-svc would carry.
    /// <paramref name="bearer"/> is only ever needed to tell "the route is not mapped" (404 from
    /// routing) apart from "no credential at all" — the kernel's fallback policy answers 401 for an
    /// anonymous request whatever the path.
    /// </summary>
    public Task<HttpResponseMessage> PostInternalAsync(
        string path,
        object? body,
        string? apiKey = InternalApiKey,
        string? idempotencyKey = null,
        string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());

        if (apiKey is not null)
        {
            request.Headers.Add(InternalRideEndpoints.ApiKeyHeader, apiKey);
        }

        Authorize(request, bearer);

        return Client.SendAsync(request);
    }

    /// <summary>
    /// GETs a path. <paramref name="apiKey"/> is for the internal plane's read routes, which carry
    /// the shared secret instead of a bearer until C042 lands a mesh.
    /// </summary>
    public Task<HttpResponseMessage> GetAsync(string path, string? bearer, string? apiKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (apiKey is not null)
        {
            request.Headers.Add(InternalRideEndpoints.ApiKeyHeader, apiKey);
        }

        Authorize(request, bearer);
        return Client.SendAsync(request);
    }

    // -------------------------------------------------------------------------------------------
    // Flow helpers — the happy path, so each test states only what it is about.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A signed quote, minted with the key ride-svc verifies against. Equivalent to what fare-svc
    /// returns; <c>FareStubTests</c> is where the real service's token is used instead.
    /// </summary>
    public string IssueFareToken(
        string vehicleType = "three_wheeler",
        string kind = "passenger",
        long amountMinor = 74_000,
        long surchargeMinor = 0) =>
        FareTokens.Issue(vehicleType, kind, amountMinor, surchargeMinor, 9.2, Pickup, Dropoff);

    /// <summary>Books a ride and returns the 202 body, failing the test on anything else.</summary>
    public async Task<JsonElement> RequestRideAsync(
        string passengerBearer,
        string? clientRequestId = null,
        string vehicleType = "three_wheeler",
        string? fareEstimateToken = null,
        string? idempotencyKey = null)
    {
        var response = await PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = clientRequestId ?? Guid.NewGuid().ToString(),
                pickup = new { lat = Pickup.Latitude, lng = Pickup.Longitude, address = "Colombo Fort" },
                dropoff = new { lat = Dropoff.Latitude, lng = Dropoff.Longitude, address = "Dehiwala" },
                vehicleType,
                fareEstimateToken = fareEstimateToken ?? IssueFareToken(vehicleType),
                paymentMethod = "cash",
            },
            passengerBearer,
            idempotencyKey);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    /// <summary>Books a proxy ride for a third party (P-01) and returns the 202 body.</summary>
    public async Task<HttpResponseMessage> RequestProxyRideAsync(
        string bookerBearer,
        string riderPhone,
        string riderName = "Nimal",
        string vehicleType = "three_wheeler",
        string? clientRequestId = null) =>
        await PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = clientRequestId ?? Guid.NewGuid().ToString(),
                kind = "proxy",
                pickup = new { lat = Pickup.Latitude, lng = Pickup.Longitude },
                dropoff = new { lat = Dropoff.Latitude, lng = Dropoff.Longitude },
                vehicleType,
                fareEstimateToken = IssueFareToken(vehicleType),
                paymentMethod = "cash",
                riderName,
                riderPhone,
            },
            bookerBearer);

    /// <summary>Books a package delivery (P-06) and returns the 202 body, pickup code included.</summary>
    public async Task<HttpResponseMessage> RequestPackageRideAsync(
        string senderBearer,
        string recipientPhone,
        string packageSize = "S",
        string vehicleType = "motorbike",
        string paymentMethod = "cash",
        string? clientRequestId = null) =>
        await PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = clientRequestId ?? Guid.NewGuid().ToString(),
                kind = "package",
                pickup = new { lat = Pickup.Latitude, lng = Pickup.Longitude },
                dropoff = new { lat = Dropoff.Latitude, lng = Dropoff.Longitude },
                vehicleType,
                fareEstimateToken = IssueFareToken(vehicleType, kind: "package"),
                paymentMethod,
                packageSize,
                packageDescription = "One box of mangoes",
                recipientName = "Kamala",
                recipientPhone,
            },
            senderBearer);

    /// <summary>
    /// Books a package and walks it to <paramref name="state"/> through the real HTTP surface, the
    /// way <see cref="DriveToAsync"/> does for a passenger ride.
    /// </summary>
    /// <remarks>
    /// The only difference is the gate: a package leaves <c>Accepted</c> through
    /// <c>package/pickup-otp</c> and not through <c>start</c>, which is exactly the substitution
    /// ADD §11.16 makes.
    /// </remarks>
    public async Task<LivePackage> DrivePackageToAsync(
        string state, string paymentMethod = "cash", string vehicleType = "motorbike")
    {
        var sender = await CreateUserAsync();
        var recipient = IamLookupStub.UnregisteredPhone();
        var driver = await CreateDriverAsync(vehicleType);

        var response = await RequestPackageRideAsync(
            sender.Bearer, recipient, vehicleType: vehicleType, paymentMethod: paymentMethod);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var booked = await ReadJsonAsync(response);
        var rideId = booked.GetProperty("rideId").GetGuid();
        var pickupOtp = booked.GetProperty("pickupOtp").GetString()!;

        var package = new LivePackage(
            rideId, sender, recipient, driver, booked.GetProperty("version").GetInt64(), pickupOtp);

        if (state == "Requested")
        {
            return package;
        }

        var offer = await OfferAsync(rideId, driver);
        package = package with { Version = offer.Version };

        if (state == "Offered")
        {
            return package;
        }

        var accepted = await PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        package = package with { Version = (await ReadJsonAsync(accepted)).GetProperty("version").GetInt64() };

        if (state == "Accepted")
        {
            return package;
        }

        var picked = await PostAsync(
            $"/v1/rides/{rideId}/package/pickup-otp", new { otp = pickupOtp }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
        package = package with { Version = (await ReadJsonAsync(picked)).GetProperty("version").GetInt64() };

        if (state == "InProgress")
        {
            return package;
        }

        // The delivery code is minted at pickup and leaves on `package.picked_up`, which is the
        // only place a test can read it — exactly where notification-svc reads it in production.
        var handover = await ReadEventPayloadAsync(rideId, "package.picked_up");
        var deliveryOtp = handover.GetProperty("payload").GetProperty("deliveryOtp").GetString()!;

        if (state == "PickedUp")
        {
            return package with { DeliveryOtp = deliveryOtp };
        }

        var delivered = await PostAsync(
            $"/v1/rides/{rideId}/package/delivery-otp", new { otp = deliveryOtp }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);

        return package with
        {
            Version = (await ReadJsonAsync(delivered)).GetProperty("version").GetInt64(),
            DeliveryOtp = deliveryOtp,
        };
    }

    /// <summary>Issues a location request as the booker and returns the raw response.</summary>
    public Task<HttpResponseMessage> IssueLocationRequestAsync(string bookerBearer, string riderPhone) =>
        PostAsync("/v1/location-requests", new { riderPhone }, bookerBearer);

    /// <summary>One <c>rides.location_requests</c> row, straight from the table.</summary>
    public async Task<(string State, double? Lat, double? Lng)> ReadLocationRequestAsync(Guid requestId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<(string, double?, double?)>(
            """
            SELECT state,
                   ST_Y(resolved_geo::geometry) AS lat,
                   ST_X(resolved_geo::geometry) AS lng
              FROM rides.location_requests
             WHERE request_id = @RequestId;
            """,
            new { RequestId = requestId });
    }

    /// <summary>The P-12 audit trail for a booker, newest first.</summary>
    public async Task<IReadOnlyList<string>> ReadLocationAuditAsync(Guid bookerId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<string>(
            "SELECT decision FROM safety.location_request_audit WHERE booker_id = @BookerId ORDER BY ts, id;",
            new { BookerId = bookerId })];
    }

    /// <summary>The proof artifacts a ride has collected (P-10).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Kind, string StorageUrl, byte[] Sha256)>> ReadProofArtifactsAsync(
        Guid rideId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<(Guid, string, string, byte[])>(
            "SELECT id, kind, storage_url, sha256 FROM rides.proof_artifacts WHERE ride_id = @RideId ORDER BY captured_at;",
            new { RideId = rideId })];
    }

    /// <summary>The unfired timers of a kind on a ride (R-04).</summary>
    public async Task<int> CountLiveTimersAsync(Guid rideId, string kind)
    {
        await using var connection = await OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM rides.timers WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL;",
            new { RideId = rideId, Kind = kind });
    }

    /// <summary>Pulls a ride's timer forward so a test can fire it without waiting out its window.</summary>
    public async Task DueTimerAsync(Guid rideId, string kind)
    {
        await using var connection = await OpenAsync();

        var updated = await connection.ExecuteAsync(
            """
            UPDATE rides.timers SET fire_at = now() - interval '1 second'
             WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL;
            """,
            new { RideId = rideId, Kind = kind });

        Assert.True(updated > 0, $"No live '{kind}' timer on ride {rideId} to bring forward.");
    }

    /// <summary>
    /// Drives the two moves dispatch-svc owns — <c>Requested → Matching → Offered</c> — and
    /// returns the offer the driver may now accept.
    /// </summary>
    public async Task<LiveOffer> OfferAsync(Guid rideId, SeededDriver driver, int? ttlSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        var matching = await PostInternalAsync($"/v1/internal/rides/{rideId}/matching", new { });
        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);

        return await OfferOnlyAsync(rideId, driver, ttlSeconds);
    }

    /// <summary>The second half of <see cref="OfferAsync"/>, for a ride already in Matching.</summary>
    public async Task<LiveOffer> OfferOnlyAsync(Guid rideId, SeededDriver driver, int? ttlSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        var offerId = Guid.NewGuid();
        var offered = await PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer",
            new
            {
                offerId = offerId.ToString(),
                driverId = driver.DriverId.ToString(),
                vehicleId = driver.VehicleId.ToString(),
                ttlSeconds,
            });

        Assert.Equal(HttpStatusCode.OK, offered.StatusCode);
        var body = await ReadJsonAsync(offered);

        return new LiveOffer(rideId, offerId, body.GetProperty("version").GetInt64());
    }

    /// <summary>
    /// Books a ride and walks it to <paramref name="state"/> through the real HTTP surface.
    /// </summary>
    /// <remarks>
    /// Every §11.12 test needs a ride sitting in one particular state, and hand-rolling the walk in
    /// each of them is how the setup and the assertion start disagreeing. Driven through the
    /// endpoints rather than by <c>UPDATE</c> so the ride under test carries the audit trail, the
    /// outbox rows and the durable timers it would carry in production — which is exactly what the
    /// cancellation and timer tests then assert about.
    /// </remarks>
    public async Task<LiveRide> DriveToAsync(string state, string? vehicleType = null)
    {
        var passengerId = await CreatePassengerAsync();
        var passenger = Tokens.Passenger(passengerId);
        var driver = await CreateDriverAsync(vehicleType ?? "three_wheeler");

        var booked = await RequestRideAsync(passenger, vehicleType: vehicleType ?? "three_wheeler");
        var rideId = booked.GetProperty("rideId").GetGuid();
        var ride = new LiveRide(rideId, passengerId, passenger, driver, booked.GetProperty("version").GetInt64());

        if (state == "Requested")
        {
            return ride;
        }

        var matching = await PostInternalAsync($"/v1/internal/rides/{rideId}/matching", new { });
        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);
        ride = ride with { Version = (await ReadJsonAsync(matching)).GetProperty("version").GetInt64() };

        if (state == "Matching")
        {
            return ride;
        }

        var offer = await OfferOnlyAsync(rideId, driver);
        ride = ride with { Version = offer.Version, OfferId = offer.OfferId };

        if (state == "Offered")
        {
            return ride;
        }

        var accepted = await PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        ride = ride with { Version = (await ReadJsonAsync(accepted)).GetProperty("version").GetInt64() };

        if (state == "Accepted")
        {
            return ride;
        }

        if (state is not ("DriverArrived" or "InProgress" or "PaymentPending"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state), state,
                "DriveToAsync walks the non-terminal states only; a terminal state is what a test asserts, " +
                "and Completed is transient — `complete` moves through it to PaymentPending in one transaction.");
        }

        ride = ride with { Version = await AdvanceAsync(ride, "arrive") };

        if (state == "DriverArrived")
        {
            return ride;
        }

        ride = ride with { Version = await AdvanceAsync(ride, "start") };

        if (state == "InProgress")
        {
            return ride;
        }

        return ride with { Version = await AdvanceAsync(ride, "complete") };
    }

    /// <summary>One driver-side move, asserting the 200 and returning the new version.</summary>
    public async Task<long> AdvanceAsync(LiveRide ride, string command)
    {
        ArgumentNullException.ThrowIfNull(ride);

        var response = await PostAsync(
            $"/v1/rides/{ride.RideId}/{command}", new { version = ride.Version }, ride.Driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ReadJsonAsync(response)).GetProperty("version").GetInt64();
    }

    /// <summary>The current state and version, read straight from the row.</summary>
    public async Task<(string State, long Version)> ReadRideAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return await connection.QuerySingleAsync<(string, long)>(
            "SELECT state, version FROM rides.rides WHERE id = @RideId;", new { RideId = rideId });
    }

    /// <summary>The ride's <c>rides.transitions</c> audit, oldest first (ADD Appendix B.2 invariant 4).</summary>
    public async Task<IReadOnlyList<(string? FromState, string ToState, string? ReasonCode, string ActorType)>>
        ReadTransitionsAsync(Guid rideId)
    {
        await using var connection = await OpenAsync();

        return [.. await connection.QueryAsync<(string?, string, string?, string)>(
            """
            SELECT from_state, to_state, reason_code, actor_type
              FROM rides.transitions WHERE ride_id = @RideId ORDER BY ts, id;
            """,
            new { RideId = rideId })];
    }

    /// <summary>The outbox rows this ride has produced, oldest first.</summary>
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
            "SELECT payload::text FROM rides.outbox WHERE aggregate_id = @RideId AND event_type = @EventType ORDER BY id DESC LIMIT 1;",
            new { RideId = rideId, EventType = eventType });

        Assert.NotNull(payload);

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Drives one pass of the R-04 durable-timer sweep, the way the hosted worker would.
    /// </summary>
    /// <remarks>
    /// The worker is registered but not hosted in tests (<c>Ride:TimersEnabled=false</c>), so a
    /// sweep happens exactly when a test asks for one. Waiting on a ticker instead would make every
    /// timer assertion a race against the assertion before it.
    /// </remarks>
    public Task<Timers.RideTimerSweep> SweepTimersAsync() =>
        Services.GetRequiredService<Timers.RideTimerWorker>()
            .SweepOnceAsync(TestContext.Current.CancellationToken);

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-RD-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _iam.DisposeAsync();

        // Delivery photos are evidence in production and litter in a test run.
        try
        {
            if (Directory.Exists(_proofPhotoRoot))
            {
                Directory.Delete(_proofPhotoRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still open somewhere is not worth failing a passing test over.
        }
    }

    /// <summary>
    /// A number no other seed in this run has used.
    /// </summary>
    /// <remarks>
    /// <b>A counter, not <c>Random.Shared</c>, and the difference is a flaky suite.</b>
    /// <c>iam.users.phone</c> is UNIQUE and this collection shares one database that nothing ever
    /// truncates, so every user seeded by every test in the run accumulates. A random draw from
    /// seven digits collides on the birthday bound long before the pool is exhausted — a few
    /// hundred users is already a couple of percent per run — and it surfaces as
    /// <c>23505 duplicate key value violates unique constraint "users_phone_key"</c> in whichever
    /// test happened to draw second. Seen on 2026-08-24 in <c>SagaStateTests</c>.
    /// <c>IamHarness</c> and <c>ReputationHarness</c> already mint theirs this way.
    /// </remarks>
    private static string NextPhone() =>
        "+9477" + (Interlocked.Increment(ref _phoneCounter) % 10_000_000).ToString("D7", CultureInfo.InvariantCulture);

    private static long _phoneCounter;

    private static void Authorize(HttpRequestMessage request, string? bearer)
    {
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }
    }
}

/// <summary>A driver plus the one APPROVED vehicle they are live on (US-9.6).</summary>
internal sealed record SeededDriver(
    Guid DriverId, Guid VehicleId, string Plate, string Name, string Phone, string Bearer);

/// <summary>An <c>iam.users</c> row, with the number the P-03 lookup resolves it by.</summary>
internal sealed record SeededUser(Guid Id, string Phone, string Bearer);

/// <summary>A package delivery sitting in a known state, with every credential it needs to hand.</summary>
/// <param name="PickupOtp">What the 202 showed the sender, once (P-07).</param>
/// <param name="DeliveryOtp">
/// What the recipient was sent, read off <c>package.picked_up</c> — <see langword="null"/> until the
/// parcel has been picked up, because it does not exist before then.
/// </param>
internal sealed record LivePackage(
    Guid RideId,
    SeededUser Sender,
    string RecipientPhone,
    SeededDriver Driver,
    long Version,
    string PickupOtp,
    string? DeliveryOtp = null);

/// <summary>An offer that has been placed and not yet answered.</summary>
/// <param name="Version">The ride version the driver must echo on the accept.</param>
internal sealed record LiveOffer(Guid RideId, Guid OfferId, long Version);

/// <summary>A ride sitting in a known state, with both parties' credentials to hand.</summary>
/// <param name="Version">What the next mutation must echo.</param>
/// <param name="OfferId">Set once the ride has reached Offered.</param>
internal sealed record LiveRide(
    Guid RideId,
    Guid PassengerId,
    string PassengerBearer,
    SeededDriver Driver,
    long Version,
    Guid? OfferId = null);
