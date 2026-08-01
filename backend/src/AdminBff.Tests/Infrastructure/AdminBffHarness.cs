using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MageRide.AdminBff;
using MageRide.Shared.Auth;
using MageRide.Shared.Http;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.AdminBff.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29, AL-07), signed by a key this run owns.
/// </summary>
/// <remarks>
/// Every token this issuer makes carries <c>app=admin</c>, because that is the only surface
/// admin-bff serves (AL-02) — and the roles are repeated claims, so a multi-role account is
/// expressible, which is what URD §2.1's union rule needs to be testable.
/// </remarks>
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

    public string Admin(Guid userId) => Issue(userId, MageRideApps.Admin, MageRideRoles.Admin);

    public string SuperAdmin(Guid userId) => Issue(userId, MageRideApps.Admin, MageRideRoles.SuperAdmin);

    /// <summary>Any one of the nine, on the Admin Portal surface.</summary>
    public string Internal(Guid userId, string role) => Issue(userId, MageRideApps.Admin, role);

    /// <summary>A driver's token: an end-user role reaching a back-office surface (AL-02's fence).</summary>
    public string Driver(Guid userId) => Issue(userId, MageRideApps.Driver, MageRideRoles.Driver);

    public string Issue(Guid userId, string app, params string[] roles)
    {
        var now = DateTime.UtcNow;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            // Repeated for the union (AL-06). A string[] is what the handler turns into several
            // claims of one name, which is the shape iam-svc's AccessTokenIssuer emits.
            [MageRideClaims.Role] = roles,
            [MageRideClaims.App] = app,
            [MageRideClaims.DeviceId] = "test-device",
        };

        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(30),
            SigningCredentials = _credentials,
        });
    }
}

/// <summary>
/// A running admin-bff on a real socket, against a real Postgres and a stub for the four upstreams.
/// </summary>
/// <remarks>
/// Built through <see cref="AdminBffApplication.Build"/>, so the pipeline under test — the bearer
/// handler, the URD §2.3 authorization handler, the D-35 endpoint filter, the problem+json handler
/// and both start-up guards — is the one the process runs.
/// </remarks>
internal sealed class AdminBffHarness : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AdminBffHarness(WebApplication app, StubUpstream upstream, TestTokenIssuer tokens, AdminSeed seed)
    {
        _app = app;
        Upstream = upstream;
        Tokens = tokens;
        Seed = seed;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        // Redirects are not followed: AL-39's document viewer answers 302 with a signed
        // object-storage URL, and the assertion is about that Location header. Following it would
        // send the test at a bucket that does not exist on this box.
        Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public HttpClient Client { get; }

    public StubUpstream Upstream { get; }

    public TestTokenIssuer Tokens { get; }

    public AdminSeed Seed { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>Every route the built application actually serves.</summary>
    public IReadOnlyList<RouteEndpoint> Routes =>
    [
        .. _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
    ];

    public static async Task<AdminBffHarness> StartAsync(
        PostgresFixture postgres, IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var upstream = await StubUpstream.StartAsync(postgres);
        var tokens = new TestTokenIssuer();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",

            // All four point at the one stub, which serves each upstream's paths. The internal key
            // is set for the two /v1/internal planes and left empty for the two role-gated ones, so
            // the stub can assert that admin-bff sent the right credential to the right kind of
            // callee.
            ["AdminBff:Upstreams:Safety:BaseUrl"] = upstream.BaseUrl,
            ["AdminBff:Upstreams:Safety:InternalApiKey"] = StubUpstream.InternalKey,
            ["AdminBff:Upstreams:Support:BaseUrl"] = upstream.BaseUrl,
            ["AdminBff:Upstreams:Support:InternalApiKey"] = StubUpstream.InternalKey,
            ["AdminBff:Upstreams:Content:BaseUrl"] = upstream.BaseUrl,
            ["AdminBff:Upstreams:Transit:BaseUrl"] = upstream.BaseUrl,

            // C063's two. Both are /v1/internal planes, so both take the shared key and are told
            // who the officer was on the body — the same split C052 and C053 use.
            ["AdminBff:Upstreams:Registry:BaseUrl"] = upstream.BaseUrl,
            ["AdminBff:Upstreams:Registry:InternalApiKey"] = StubUpstream.InternalKey,
            ["AdminBff:Upstreams:Fleet:BaseUrl"] = upstream.BaseUrl,
            ["AdminBff:Upstreams:Fleet:InternalApiKey"] = StubUpstream.InternalKey,

            // C065's two, and they are the two kinds. wallet-svc's ledger seam is an
            // /v1/internal plane and takes the shared key; fare-svc's refund is a role-gated
            // /v1/admin route and gets the operator's own bearer forwarded instead — which is
            // exactly the split the stub asserts on.
            ["AdminBff:Upstreams:Wallet:BaseUrl"] = upstream.BaseUrl,
            ["AdminBff:Upstreams:Wallet:InternalApiKey"] = StubUpstream.InternalKey,
            ["AdminBff:Upstreams:Fare:BaseUrl"] = upstream.BaseUrl,

            // A fixed key so a signed object URL is reproducible inside one run, and a base URL so
            // the 302's Location is the absolute form a bucket would serve.
            ["AdminBff:Documents:SigningKey"] = "test-document-signing-key",
            ["AdminBff:Documents:PublicBaseUrl"] = "https://docs.mageride.test",

            // The audit ROW is what D-35 is about and what these tests assert on; the topic is
            // D6' §2.1's sink and there is no broker in this suite.
            ["AdminBff:Audit:PublishToTopic"] = "false",

            // Off, so a timer cannot materialise a day underneath an assertion about the rollup —
            // the same reason C060's and C061's harnesses leave their runners off.
            ["Analytics:RollupEnabled"] = "false",

            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
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

        var app = AdminBffApplication.Build(
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

        return new AdminBffHarness(app, upstream, tokens, new AdminSeed(postgres));
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string bearer, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string bearer) =>
        SendAsync(HttpMethod.Get, path, bearer);

    public async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        await _app.StopAsync();
        await _app.DisposeAsync();
        await Upstream.DisposeAsync();
    }
}
