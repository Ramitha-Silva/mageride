using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Fleet.Endpoints;
using MageRide.Shared.Auth;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Npgsql;

namespace MageRide.Fleet.Tests.Infrastructure;

/// <summary>
/// A running fleet-svc on a real socket, against a real Postgres.
/// </summary>
/// <remarks>
/// Built through <see cref="FleetApplication.Build"/>, so the pipeline under test — the bearer
/// handler, the problem+json handler, the options validation, the idempotency middleware, the
/// fleet-access filter, the internal-key filter — is the one the process runs.
/// </remarks>
internal sealed class FleetHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret the internal plane demands until the mesh lands.</summary>
    public const string InternalApiKey = "c058-fleet-internal-key-not-a-secret";

    /// <summary>
    /// A real login role that is a member of <c>mageride_fleet_reader</c>.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the row-level-security assertions mean anything.</b> The container's
    /// own <c>mageride</c> user is a superuser, and a superuser bypasses RLS entirely — so a test
    /// that connected as it and saw the right rows would have proved only that the application's
    /// <c>WHERE</c> clause works. The service itself reaches the same place by
    /// <c>SET LOCAL ROLE</c>; <see cref="OpenAsFleetReaderAsync"/> reaches it by logging in as a
    /// role that has never been anything else.
    /// </remarks>
    public const string FleetReaderLogin = "c058_fleet_reader";

    private const string FleetReaderPassword = "c058-not-a-secret";

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;
    private readonly string _documentRoot;

    private FleetHarness(WebApplication app, PostgresFixture postgres, TestTokenIssuer tokens, string documentRoot)
    {
        _app = app;
        _postgres = postgres;
        _documentRoot = documentRoot;

        Tokens = tokens;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public IServiceProvider Services => _app.Services;

    /// <param name="configure">
    /// Runs before <c>AddFleetServices</c>, which is what lets a test register its own
    /// <c>IVehicleDocumentExtractionClient</c>: the service registers both real implementations with
    /// <c>TryAddSingleton</c>, so whatever is already there wins. That is the only way to drive
    /// AL-50's slot rule end to end without an ocr-svc on a socket.
    /// </param>
    public static async Task<FleetHarness> StartAsync(
        PostgresFixture postgres,
        IDictionary<string, string?>? settings = null,
        bool withInternalPlane = true,
        Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await EnsureFleetReaderLoginAsync(postgres);
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();

        // One directory per harness, so two tests in the shared collection cannot see each other's
        // files and a leftover bank statement cannot make a later assertion pass.
        var documentRoot = Path.Combine(Path.GetTempPath(), "mageride-c058", Guid.NewGuid().ToString("N"));

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's
            // auth wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",

            ["Fleet:InternalApiKey"] = withInternalPlane ? InternalApiKey : null,
            ["Fleet:DocumentRoot"] = documentRoot,

            ["urls"] = "http://127.0.0.1:0",
            // One /metrics endpoint per harness would collide across concurrently running tests.
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = FleetApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                configure?.Invoke(builder);

                // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(overrides);

                // PostConfigure so this runs after the kernel's AddMageRideAuth has built the options.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        return new FleetHarness(app, postgres, tokens, documentRoot);
    }

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    public async Task<HttpResponseMessage> GetAsync(string path, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(request, bearer);
        return await Client.SendAsync(request);
    }

    public async Task<T> GetAsync<T>(string path, string? bearer = null)
    {
        using var response = await GetAsync(path, bearer);
        return await OkAsync<T>(response, $"GET {path}");
    }

    /// <summary>POSTs with a fresh <c>Idempotency-Key</c> — what a well-behaved client does once.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? bearer = null) =>
        PostWithKeyAsync(path, body, bearer, Guid.NewGuid().ToString());

    /// <summary>POSTs and deserialises the success body, failing loudly on anything else.</summary>
    public async Task<T> PostJsonAsync<T>(string path, object? body, string? bearer = null)
    {
        using var response = await PostAsync(path, body, bearer);
        return await OkAsync<T>(response, $"POST {path}");
    }

    /// <summary>POSTs with a caller-chosen key, so a retry can be replayed (R-14).</summary>
    public async Task<HttpResponseMessage> PostWithKeyAsync(
        string path, object? body, string? bearer, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, idempotencyKey);
        Authorize(request, bearer);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PutAsync(string path, object? body, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        Authorize(request, bearer);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public async Task<T> PutAsync<T>(string path, object? body, string? bearer = null)
    {
        using var response = await PutAsync(path, body, bearer);
        return await OkAsync<T>(response, $"PUT {path}");
    }

    /// <summary>Uploads bytes as <c>multipart/form-data</c>, the way SCR-FP-002a does.</summary>
    public async Task<HttpResponseMessage> UploadPayoutDocumentAsync(
        Guid fleetId, string bearer, string kind, byte[] bytes, string fileName = "statement.png")
    {
        ArgumentNullException.ThrowIfNull(bytes);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/fleets/{fleetId}/payout-profile/documents");

        Authorize(request, bearer);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        var content = new MultipartFormDataContent();
        content.Add(new StringContent(kind), "kind");

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", fileName);

        request.Content = content;

        return await Client.SendAsync(request);
    }

    /// <summary>The internal plane, called the way admin-bff would.</summary>
    public async Task<HttpResponseMessage> InternalAsync(
        HttpMethod method, string path, object? body = null, string? apiKey = InternalApiKey)
    {
        using var request = new HttpRequestMessage(method, path);

        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(InternalFleetEndpoints.ApiKeyHeader, apiKey);
        }

        if (method == HttpMethod.Post)
        {
            request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public async Task<T> InternalAsync<T>(
        HttpMethod method, string path, object? body = null, string? apiKey = InternalApiKey)
    {
        using var response = await InternalAsync(method, path, body, apiKey);
        return await OkAsync<T>(response, $"{method} {path}");
    }

    public static async Task<T> OkAsync<T>(HttpResponseMessage response, string what)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode is >= 200 and < 300, $"{what} returned {(int)response.StatusCode}: {text}");

        return JsonSerializer.Deserialize<T>(text, MageRideJson.Options)!;
    }

    /// <summary>The RFC 7807 <c>type</c> code and the raw body of a failed response.</summary>
    public static async Task<(HttpStatusCode Status, string Code, string Body)> ProblemAsync(
        HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(text);

        var type = document.RootElement.TryGetProperty("type", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;

        return (response.StatusCode, type.Split('/')[^1], text);
    }

    private static void Authorize(HttpRequestMessage request, string? bearer)
    {
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Seeding and asserting against the rows
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>
    /// A connection as a role that is <b>not</b> a superuser and holds only the fleet grants.
    /// </summary>
    /// <remarks>
    /// The one way this suite can ask the database a question the way a fleet reader would.
    /// Every read through it is subject to migration 1806's policies, and every read through
    /// <see cref="OpenAsync"/> is not — which is the whole distinction the definition of done
    /// turns on.
    /// </remarks>
    public async Task<NpgsqlConnection> OpenAsFleetReaderAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            Username = FleetReaderLogin,
            Password = FleetReaderPassword,
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    /// <summary>An <c>iam.users</c> row, so every foreign key onto it is satisfiable.</summary>
    public async Task<Guid> CreateUserAsync(string role = "fleet_owner", string? email = null)
    {
        var id = Guid.CreateVersion7();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, email, role) VALUES (@Id, @Phone, @Email, @Role);",
            new
            {
                Id = id,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
                Email = email,
                Role = role,
            });

        return id;
    }

    /// <summary>A Fleet Owner and a bearer for them, with no organisation yet.</summary>
    public async Task<(Guid Id, string Bearer)> CreateFleetOwnerAsync()
    {
        var id = await CreateUserAsync();
        return (id, Tokens.FleetOwner(id));
    }

    /// <summary>
    /// A registered organisation, through the real route, and a bearer scoped to it.
    /// </summary>
    /// <remarks>
    /// Through the API rather than by INSERT: the owner's <c>iam.fleet_members</c> seat is created
    /// by the same transaction as the organisation, and a fixture that wrote only the
    /// <c>registry.fleets</c> row would be testing against a state the service cannot produce.
    /// </remarks>
    public async Task<SeededFleet> CreateFleetAsync(string? name = null, string? businessReg = null)
    {
        var (ownerId, ownerBearer) = await CreateFleetOwnerAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];

        var response = await PostAsync(
            "/v1/fleets",
            new
            {
                name = name ?? $"Test Transit {suffix}",
                registrationNo = businessReg ?? $"PV-{suffix}",
                contactPhone = "+94771234567",
                contactEmail = $"ops-{suffix}@example.lk",
                address = "42 Galle Road, Colombo 03",
            },
            ownerBearer);

        var fleet = await OkAsync<FleetResponse>(response, "POST /v1/fleets");
        var fleetId = Guid.Parse(fleet.FleetId);

        return new SeededFleet(fleetId, ownerId, Tokens.FleetMember(ownerId, fleetId, FleetRoles.Owner));
    }

    /// <summary>Approves an organisation the way a Verification Officer would, through the plane admin-bff uses.</summary>
    public async Task ApproveAsync(Guid fleetId)
    {
        var officerId = await CreateUserAsync("verification_officer");

        using var response = await InternalAsync(
            HttpMethod.Post, $"/v1/internal/fleets/{fleetId}/approve", new { officerId = officerId.ToString() });

        Assert.True(
            response.IsSuccessStatusCode,
            $"approving {fleetId} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Adds a Mode A or Mode B vehicle to a fleet — C059's route, so this suite writes the rows.</summary>
    public async Task<Guid> AddVehicleAsync(Guid fleetId, Guid ownerId, string mode = "B", string type = "van")
    {
        var vehicleId = Guid.CreateVersion7();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles (id, owner_id, registration_number, vehicle_type, mode, driver_name)
            VALUES (@Id, @OwnerId, @Registration, @Type, @Mode, 'Test Driver');
            INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode) VALUES (@FleetId, @Id, @Mode);
            """,
            new
            {
                Id = vehicleId,
                OwnerId = ownerId,
                Registration = "TST-" + Random.Shared.NextInt64(100_000, 999_999).ToString(CultureInfo.InvariantCulture),
                Type = type,
                Mode = mode,
                FleetId = fleetId,
            });

        return vehicleId;
    }

    /// <summary>A driver account with the canonical role an assignment demands (US-13.2).</summary>
    public async Task<(Guid Id, string Phone)> CreateDriverAsync()
    {
        var id = Guid.CreateVersion7();
        var phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'driver');",
            new { Id = id, Phone = phone });

        return (id, phone);
    }

    /// <summary>Uploads a document into one of SCR-FP-004's named slots.</summary>
    public async Task<HttpResponseMessage> UploadVehicleDocumentAsync(
        Guid fleetId, Guid vehicleId, string bearer, string kind, string? expiresAt = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/fleets/{fleetId}/vehicles/{vehicleId}/documents");

        Authorize(request, bearer);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        var content = new MultipartFormDataContent { { new StringContent(kind), "kind" } };

        if (expiresAt is not null)
        {
            content.Add(new StringContent(expiresAt), "expiresAt");
        }

        var file = new ByteArrayContent([1, 2, 3, 4, 5, 6, 7, 8]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", $"{kind}.png");

        request.Content = content;

        return await Client.SendAsync(request);
    }

    /// <summary>Uploads a bulk-onboarding CSV the way SCR-FP-004's importer does.</summary>
    public async Task<HttpResponseMessage> UploadVehicleCsvAsync(Guid fleetId, string bearer, string csv)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/fleets/{fleetId}/vehicles/bulk");

        Authorize(request, bearer);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "vehicles.csv");

        request.Content = content;

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> DeleteAsync(string path, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        Authorize(request, bearer);
        return await Client.SendAsync(request);
    }

    /// <summary>A telemetry sample for a fleet vehicle, as the hot path would have written it.</summary>
    public async Task AddPositionAsync(
        Guid fleetId, Guid vehicleId, double lat, double lng, DateTimeOffset? sampleTs = null, long seq = 1)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO telemetry.positions
              (vehicle_id, sample_ts, seq, lat, lng, speed_mps, heading_deg, source, fleet_id)
            VALUES (@VehicleId, @SampleTs, @Seq, @Lat, @Lng, 12.5, 90, 0, @FleetId);
            """,
            new
            {
                VehicleId = vehicleId,
                SampleTs = sampleTs ?? DateTimeOffset.UtcNow,
                Seq = seq,
                Lat = lat,
                Lng = lng,
                FleetId = fleetId,
            });
    }

    /// <summary>Opens a Mode A/B tracking session, which is what a departure being "started" means.</summary>
    public async Task<Guid> StartSessionAsync(
        Guid vehicleId, Guid driverId, DateTimeOffset startedAt, string mode = "B")
    {
        var id = Guid.CreateVersion7();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO trips.sessions (id, vehicle_id, driver_id, mode, state, started_at)
            VALUES (@Id, @VehicleId, @DriverId, @Mode, 'ACTIVE', @StartedAt);
            """,
            new { Id = id, VehicleId = vehicleId, DriverId = driverId, Mode = mode, StartedAt = startedAt });

        return id;
    }

    /// <summary>What `registry.driver_eligible_vehicles` says this driver may go live on.</summary>
    /// <remarks>
    /// The projection registry-svc's select-live, dispatch-svc's standby gate and trip-state-svc's
    /// session start all read (migration 0310/0314) — so asserting against it is asserting against
    /// what the Driver App will actually offer, without booting three services.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> EligibleVehiclesAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<Guid>(
            "SELECT vehicle_id FROM registry.driver_eligible_vehicles WHERE driver_id = @DriverId;",
            new { DriverId = driverId });

        return [.. rows];
    }

    public async Task<string?> VehicleStatusAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<string?>(
            "SELECT status FROM registry.vehicles WHERE id = @Id;", new { Id = vehicleId });
    }

    /// <summary>Every version of an org's payout profile, oldest first, as it is stored.</summary>
    public async Task<IReadOnlyList<(Guid Id, string Status, string AccountNo, DateTimeOffset? VerifiedAt)>>
        PayoutVersionsAsync(Guid fleetId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid, string, string, DateTimeOffset?)>(
            """
            SELECT id, status, account_no, verified_at
              FROM registry.fleet_payout_profiles
             WHERE fleet_id = @FleetId
             ORDER BY created_at, id;
            """,
            new { FleetId = fleetId });

        return [.. rows];
    }

    /// <summary>
    /// The exact query subscription-svc's pay sheet runs (C050 <c>ReadVerifiedPayoutProfileAsync</c>).
    /// </summary>
    /// <remarks>
    /// Reproduced rather than referenced, so this suite does not boot a second service to answer
    /// one question — and asserted against verbatim, because "Paid subscriptions keep collecting
    /// against the last verified snapshot" <em>means</em> that this query still returns the old
    /// account.
    /// </remarks>
    public async Task<(string Bank, string AccountNo)?> PaySheetPayToAsync(Guid fleetId)
    {
        await using var connection = await _postgres.OpenAsync();

        var row = await connection.QuerySingleOrDefaultAsync<(string, string)?>(
            """
            SELECT p.bank, p.account_no
              FROM registry.fleet_payout_profiles p
             WHERE p.fleet_id = @FleetId AND p.status = 'verified';
            """,
            new { FleetId = fleetId });

        return row;
    }

    public async Task<string?> VehicleBillingAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<string?>(
            "SELECT mode_b_billing FROM registry.vehicles WHERE id = @Id;", new { Id = vehicleId });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(10));
            await _app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop the harness service: {exception.Message}");
        }

        try
        {
            if (Directory.Exists(_documentRoot))
            {
                Directory.Delete(_documentRoot, recursive: true);
            }
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"warning: could not remove {_documentRoot}: {exception.Message}");
        }
    }

    /// <summary>
    /// Creates the non-superuser login the RLS assertions connect as.
    /// </summary>
    /// <remarks>
    /// A role, not a fixture row, so it is created once per container and left in place — roles
    /// are cluster-scoped and <c>CREATE ROLE IF NOT EXISTS</c> does not exist, hence the guard.
    /// The same shape <c>infra/scripts/migrate-verify.sh</c> uses for its own <c>verify_fleet</c>.
    /// </remarks>
    private static async Task EnsureFleetReaderLoginAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            $"""
             DO $$
             BEGIN
               IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{FleetReaderLogin}') THEN
                 CREATE ROLE {FleetReaderLogin} LOGIN PASSWORD '{FleetReaderPassword}';
               END IF;
             END $$;
             GRANT mageride_fleet_reader TO {FleetReaderLogin};
             GRANT CONNECT ON DATABASE {connection.Database} TO {FleetReaderLogin};
             """);
    }

    /// <summary>Empties what this service owns, plus the rows its tests create in other schemas.</summary>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        // registry.fleets CASCADEs to fleet_vehicles, fleet_assignments, fleet_payout_profiles and
        // iam.fleet_members; iam.users is truncated last because everything references it.
        // registry.fleets CASCADEs to fleet_vehicles, fleet_assignments, fleet_payout_profiles,
        // fleet_schedules, fleet_bulk_jobs, spatial.geofences and iam.fleet_members; the two
        // telemetry relations and registry.documents are named because nothing cascades to them —
        // a position left behind by one test is a marker on the next one's map.
        await connection.ExecuteAsync(
            """
            TRUNCATE registry.fleet_command_log;
            TRUNCATE telemetry.positions;
            TRUNCATE trips.sessions CASCADE;
            TRUNCATE registry.documents CASCADE;
            TRUNCATE registry.fleets CASCADE;
            TRUNCATE registry.vehicles CASCADE;
            TRUNCATE docs.uploads CASCADE;
            TRUNCATE iam.users CASCADE;
            """);
    }
}

/// <summary>An organisation this suite created, and a bearer scoped to it.</summary>
internal sealed record SeededFleet(Guid FleetId, Guid OwnerId, string OwnerBearer);
