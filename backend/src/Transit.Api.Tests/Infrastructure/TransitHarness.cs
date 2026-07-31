using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MageRide.Shared.Auth;
using MageRide.Shared.Http;
using MageRide.TestKit;
using MageRide.Transit.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Transit.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this test run owns.
/// </summary>
internal sealed class TestTokenIssuer
{
    private const string Issuer = "https://iam.mageride.test";

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly SigningCredentials _credentials;

    public TestTokenIssuer()
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        _credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        PublicKey = new RsaSecurityKey(_rsa.ExportParameters(includePrivateParameters: false)) { KeyId = "test-key" };
    }

    public SecurityKey PublicKey { get; }

    public string IssuerName => Issuer;

    public string Passenger() => Issue(Guid.NewGuid(), MageRideRoles.Passenger, MageRideApps.Passenger);

    /// <summary>An Admin Portal session (AL-07: the portals sign in by password/Google, not OTP).</summary>
    public string Admin(Guid userId) => Issue(userId, MageRideRoles.Admin, MageRideApps.Admin);

    /// <summary>Δ C057 — one of the four back-office roles SCR-AP-016 denies (AL-06).</summary>
    public string Internal(Guid userId, string role) => Issue(userId, role, MageRideApps.Admin);

    public string Issue(Guid userId, string role, string app)
    {
        var now = DateTime.UtcNow;

        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [MageRideClaims.Role] = role,
                [MageRideClaims.App] = app,
                [MageRideClaims.DeviceId] = "test-device",
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(30),
            SigningCredentials = _credentials,
        });
    }
}

/// <summary>
/// A running transit-svc on a real socket, against a real Postgres and a stub URL shortener.
/// </summary>
/// <remarks>
/// Built through <see cref="TransitApplication.Build"/>, so the pipeline under test — the bearer
/// handler, the problem+json handler, the options validation and the <c>LISTEN</c>-driven feed
/// cache — is the one the process runs.
/// </remarks>
internal sealed class TransitHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Shortener _shortener;
    private readonly string _bearer;
    private readonly string _storageRoot;

    private TransitHarness(
        WebApplication app, Shortener shortener, GtfsSeed seed, TestTokenIssuer tokens, string storageRoot)
    {
        _app = app;
        _shortener = shortener;
        _bearer = tokens.Passenger();
        _storageRoot = storageRoot;

        Seed = seed;
        Tokens = tokens;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };

        // Δ C057. `…/download` answers a 302 to a signed URL and that redirect is the contract, so
        // a client that follows it silently would assert the wrong thing — this one reports it.
        Unredirected = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public HttpClient Client { get; }

    /// <inheritdoc cref="Client"/>
    public HttpClient Unredirected { get; }

    public GtfsSeed Seed { get; }

    public TestTokenIssuer Tokens { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>The stub shortener's base URL, allow-listed for this run.</summary>
    public string ShortenerBaseUrl => _shortener.BaseUrl;

    /// <summary>
    /// Removes a stored zip behind the service's back, which is what an object store losing an
    /// object looks like from here — and the cheapest way to make an activation fail *after* the
    /// version row says it may go live.
    /// </summary>
    public void LoseStoredZip(Guid feedVersionId) =>
        File.Delete(Path.Combine(_storageRoot, "gtfs", $"{feedVersionId:D}.zip"));

    /// <summary>Registers a short link and returns the URL a passenger would paste.</summary>
    public string ShortLink(string target) => _shortener.Register(target);

    public static async Task<TransitHarness> StartAsync(
        PostgresFixture postgres, IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var shortener = await Shortener.StartAsync();
        var tokens = new TestTokenIssuer();

        // Per-run, so two harnesses in one assembly cannot read each other's stored feeds and a
        // leftover zip cannot make a later test pass.
        var storageRoot = Path.Combine(Path.GetTempPath(), "mageride-gtfs-tests", Guid.NewGuid().ToString("N"));

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Δ C057 — the GTFS Dataset Manager.
            ["Transit:Gtfs:StorageRoot"] = storageRoot,
            // A fixed key, so a signed download link is verifiable and the "expired link" case is
            // a property of the expiry rather than of a key that changed.
            ["Transit:Gtfs:DownloadSigningKey"] = Convert.ToBase64String(new byte[32]),
            // The validation latch is what actually starts a validation; this only bounds the
            // reclaim path, and one second keeps the suite honest about not depending on the poll.
            ["Transit:Gtfs:ValidationPollInterval"] = "00:00:01",
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer — which matters more here than
            // usual: transaction pooling drops the LISTEN this service depends on.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",

            // The stub shortener stands in for maps.app.goo.gl, so it has to be reachable — and
            // adding it is itself the assertion that the allowlist is what decides.
            ["Transit:MapsLink:AllowedHosts:0"] = "maps.app.goo.gl",
            ["Transit:MapsLink:AllowedHosts:1"] = "goo.gl",
            ["Transit:MapsLink:AllowedHosts:2"] = "www.google.com",
            ["Transit:MapsLink:AllowedHosts:3"] = "127.0.0.1",

            // The option's floor, so the "a notification that never arrives" test does not spend
            // half a minute proving the safety net works. Every other refresh is NOTIFY-driven and
            // does not wait for this at all.
            ["Transit:FeedPollInterval"] = "00:00:05",

            ["urls"] = "http://127.0.0.1:0",
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = TransitApplication.Build(
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

                builder.Configuration.AddInMemoryCollection(overrides);

                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        return new TransitHarness(app, shortener, new GtfsSeed(postgres), tokens, storageRoot);
    }

    // -----------------------------------------------------------------------------------------
    // Δ C057 — the admin surface
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// An Admin Portal operator with a real <c>iam.users</c> row.
    /// </summary>
    /// <remarks>
    /// The row is not decoration: <c>transit.gtfs_feed_versions.uploaded_by</c> has a foreign key
    /// onto it, so an upload by a subject that does not exist fails in the database rather than in
    /// the assertion.
    /// </remarks>
    public async Task<(Guid UserId, string Bearer)> AdminAsync()
    {
        var userId = await Seed.CreateUserAsync(MageRideRoles.Admin);

        return (userId, Tokens.Admin(userId));
    }

    public async Task<HttpResponseMessage> UploadAsync(
        byte[] zip, string? bearer, string fileName = "gtfs.zip", string? idempotencyKey = null)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(zip);

        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/transit/gtfs/uploads")
        {
            Content = content,
        };

        request.Headers.TryAddWithoutValidation(
            MageRideHeaders.IdempotencyKey, idempotencyKey ?? Guid.NewGuid().ToString("N"));

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>Uploads and waits for the validation worker to reach a verdict.</summary>
    public async Task<Guid> UploadAndAwaitVerdictAsync(byte[] zip, string bearer, string fileName = "gtfs.zip")
    {
        using var response = await UploadAsync(zip, bearer, fileName);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var accepted = JsonSerializer.Deserialize<AcceptedBody>(
            await response.Content.ReadAsStringAsync(), MageRideJson.Options)!;

        await WaitForStatusAsync(accepted.FeedVersionId, bearer, status => status is "validated" or "failed");

        return accepted.FeedVersionId;
    }

    /// <summary>Polls the status endpoint the way SCR-AP-016's stepper does (2 s), but faster.</summary>
    public async Task<JsonElement> WaitForStatusAsync(
        Guid feedVersionId, string bearer, Func<string, bool> until, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(60));
        var last = default(JsonElement);

        while (DateTime.UtcNow < deadline)
        {
            using var response = await SendAsync(
                HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}", bearer);

            var payload = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, $"status poll answered {(int)response.StatusCode}: {payload}");

            last = JsonDocument.Parse(payload).RootElement.Clone();

            if (until(last.GetProperty("status").GetString()!))
            {
                return last;
            }

            await Task.Delay(150);
        }

        Assert.Fail($"the feed never reached the expected status; last was {last}");

        return last;
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? bearer, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (method == HttpMethod.Post)
        {
            request.Headers.TryAddWithoutValidation(
                MageRideHeaders.IdempotencyKey, idempotencyKey ?? Guid.NewGuid().ToString("N"));
        }

        return await Client.SendAsync(request);
    }

    /// <summary>Reads a successful JSON response, failing with the body when it is not one.</summary>
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"the request answered {(int)response.StatusCode}: {payload}");

        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    /// <summary>Reads an RFC 7807 body, whatever the status.</summary>
    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private sealed record AcceptedBody(Guid FeedVersionId);

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    public async Task<HttpResponseMessage> GetAsync(string path, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        }

        return await Client.SendAsync(request);
    }

    public async Task<T> GetAsync<T>(string path)
    {
        using var response = await GetAsync(path);

        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"GET {path} answered {(int)response.StatusCode}: {payload}");

        return JsonSerializer.Deserialize<T>(payload, MageRideJson.Options)!;
    }

    /// <summary>Waits for the running service's cache to reach a feed version, or fails.</summary>
    public async Task<T> WaitForAsync<T>(string path, Func<T, bool> until, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(60));

        T last = default!;

        while (DateTime.UtcNow < deadline)
        {
            last = await GetAsync<T>(path);

            if (until(last))
            {
                return last;
            }

            await Task.Delay(200);
        }

        Assert.Fail($"the feed cache did not reach the expected state within {within ?? TimeSpan.FromSeconds(60)}.");

        return last;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Unredirected.Dispose();

        await _app.StopAsync();
        await _app.DisposeAsync();
        await _shortener.DisposeAsync();

        try
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was uploaded in this run.
        }
    }

    /// <summary>
    /// A URL shortener on a real socket, standing in for <c>maps.app.goo.gl</c>.
    /// </summary>
    /// <remarks>
    /// <b>A server, not a stubbed handler.</b> What is under test is a redirect chain being
    /// followed by hand with the allowlist re-checked at every hop — the interesting cases are a
    /// chain that hops twice and a chain that hops somewhere it must not be followed, and neither
    /// exists above the socket.
    /// </remarks>
    private sealed class Shortener : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Dictionary<string, string> _targets = new(StringComparer.Ordinal);

        private Shortener(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public string Register(string target)
        {
            var token = Guid.NewGuid().ToString("N")[..10];

            lock (_targets)
            {
                _targets[token] = target;
            }

            return $"{BaseUrl}/{token}";
        }

        public static async Task<Shortener> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();

            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();

            Shortener? shortener = null;

            app.MapMethods("/{token}", ["GET", "HEAD"], (string token, HttpContext context) =>
            {
                string? target;

                lock (shortener!._targets)
                {
                    shortener._targets.TryGetValue(token, out target);
                }

                if (target is null)
                {
                    return Results.NotFound();
                }

                // 302, as a shortener does. The resolver reads Location and never the body.
                context.Response.Headers.Location = target;

                return Results.StatusCode(302);
            });

            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            shortener = new Shortener(app, address.TrimEnd('/'));

            return shortener;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
