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

    private TransitHarness(WebApplication app, Shortener shortener, GtfsSeed seed, TestTokenIssuer tokens)
    {
        _app = app;
        _shortener = shortener;
        _bearer = tokens.Passenger();

        Seed = seed;
        Tokens = tokens;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };
    }

    public HttpClient Client { get; }

    public GtfsSeed Seed { get; }

    public TestTokenIssuer Tokens { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>The stub shortener's base URL, allow-listed for this run.</summary>
    public string ShortenerBaseUrl => _shortener.BaseUrl;

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

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
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

        return new TransitHarness(app, shortener, new GtfsSeed(postgres), tokens);
    }

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

        await _app.StopAsync();
        await _app.DisposeAsync();
        await _shortener.DisposeAsync();
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
