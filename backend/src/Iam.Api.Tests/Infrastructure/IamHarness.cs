using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Iam.Otp;
using MageRide.Shared.Caching;
using MageRide.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Microsoft.Extensions.Hosting;

namespace MageRide.Iam.Tests.Infrastructure;

/// <summary>
/// A running iam-svc on a real socket, against a real Postgres and Redis.
/// </summary>
/// <remarks>
/// Built through <see cref="IamApplication.Build"/>, so the pipeline under test — deny-by-default
/// authorization, the idempotency middleware, the problem+json handler — is the one the process
/// runs. Kestrel rather than TestServer for the same reason C008's harness uses it: the
/// idempotency middleware swaps the response body feature, and the JWKS is fetched over HTTP by
/// the token-validation test.
/// </remarks>
internal sealed class IamHarness : IAsyncDisposable
{
    /// <summary>Deterministic pepper, so a hash computed in a test matches one the service made.</summary>
    private const string TestPepper = "c020-otp-pepper-not-a-secret";

    /// <summary>MqttOptions requires 32 characters; EMQX would validate against the same value.</summary>
    private const string MqttSecret = "c026-mqtt-session-secret-not-a-real-one";

    /// <summary>
    /// Deterministic key for <c>iam.phone_lookups.phone_hash</c> (P-03), so a test can recompute a
    /// digest the service wrote and prove the number itself is nowhere in the row.
    /// </summary>
    public const string TestPhoneHashKey = "c027-phone-hash-key-not-a-secret";

    /// <summary>The shared secret <c>GET /v1/users/lookup</c> demands until C042 lands a mesh.</summary>
    public const string InternalApiKey = "c027-internal-key-not-a-secret";

    private static int _phoneCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _app;

    private IamHarness(
        WebApplication app,
        HttpClient client,
        CapturingOtpSender sms,
        TestOidcProvider oidc,
        string baseAddress,
        string connectionString)
    {
        _app = app;
        Client = client;
        Sms = sms;
        Oidc = oidc;
        BaseAddress = baseAddress;
        Seed = new IamSeed(
            connectionString, app.Services.GetRequiredService<MageRide.Iam.Auth.PasswordHasher>());
    }

    public HttpClient Client { get; }

    public CapturingOtpSender Sms { get; }

    /// <summary>Stands in for Google and Apple — see <see cref="TestOidcProvider"/>.</summary>
    public TestOidcProvider Oidc { get; }

    /// <summary>Direct database access, for the accounts a portal sign-in cannot create itself.</summary>
    public IamSeed Seed { get; }

    /// <summary>
    /// Forgets a session's revocation tombstone, which is what a Redis eviction or restart looks
    /// like from the kernel's side (Δ MCS-30).
    /// </summary>
    /// <remarks>
    /// The JWT is taken apart here rather than in a test because the interesting part is the
    /// behaviour either side of the deletion, not the base64url. Signature unverified on purpose:
    /// this reads a claim out of a token the harness itself just minted.
    /// </remarks>
    public Task ForgetRevocationAsync(string accessToken) =>
        _app.Services.GetRequiredService<IConnectionMultiplexer>()
            .GetDatabase()
            .KeyDeleteAsync(RedisKeys.RevokedSession(JtiOf(accessToken)));

    /// <summary>The <c>jti</c> claim of an access token, without verifying it.</summary>
    public static string JtiOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1];
        var padded = payload.Replace('-', '+').Replace('_', '/').PadRight((payload.Length + 3) / 4 * 4, '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        return document.RootElement.GetProperty("jti").GetString()!;
    }

    public string BaseAddress { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>A +94 mobile no other test in this run will use.</summary>
    public static string NextPhone() =>
        "+947" + (Interlocked.Increment(ref _phoneCounter) % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>
    /// A harness with D-32's 60-second resend cooldown turned off.
    /// </summary>
    /// <remarks>
    /// The bucket keys on the phone number, so a second sign-in for the same person inside a
    /// minute is a 429 — correct, and exactly what <c>OtpPolicyTests</c> asserts. Tests about
    /// what a <em>second sign-in</em> does to a session would otherwise spend their time proving
    /// the rate limiter again, so they opt out of the cooldown and keep the hourly cap.
    /// </remarks>
    public static Task<IamHarness> StartWithoutResendCooldownAsync(PostgresFixture postgres, RedisFixture redis) =>
        StartAsync(postgres, redis, new Dictionary<string, string?> { ["Otp:ResendCooldownSec"] = "0" });

    public static async Task<IamHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? settings = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            // Never fetched: iam-svc resolves its own signing key. The kernel's auth wiring binds
            // the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Otp:PepperKey"] = TestPepper,
            // Without an accepted audience the verifier refuses every token, which is the
            // deliberate default (see OidcTokenVerifier) and would make every portal test a 403.
            ["Oidc:Google:ClientIds:0"] = TestOidcProvider.GoogleClientId,
            ["Oidc:Google:ClientSecret"] = "c026-google-client-secret",
            ["Oidc:Google:RedirectUri"] = "https://admin.mageride.lk/auth/callback",
            ["Oidc:Apple:ClientIds:0"] = TestOidcProvider.AppleClientId,
            // POST /v1/auth/mqtt-token mints against this (E-02); MqttOptions validates it on
            // start, so every harness needs one whether or not its test asks for a token.
            ["Mqtt:SessionTokenSecret"] = MqttSecret,
            // The configured floor. Production's 600 000 costs a fifth of a second per sign-in
            // and the verifier behaves identically either way; AuthPolicyOptions refuses to go
            // lower, which is the point of the floor being in the options and not here.
            ["Auth:PasswordIterations"] = "100000",
            // C027. Both are required outside Development and both are fail-fast singletons, so a
            // harness without them would not start.
            ["Auth:PhoneHashKey"] = TestPhoneHashKey,
            ["Auth:InternalApiKey"] = InternalApiKey,
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

        var sms = new CapturingOtpSender();
        var oidc = new TestOidcProvider();

        var app = IamApplication.Build(
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

                // Registered before AddIamServices, whose registrations are TryAdds.
                builder.Services.AddSingleton<IOtpSender>(sms);

                // Every TimeProvider registration in the kernel and in AddIamServices is a
                // TryAdd, so this one wins for the whole graph — which is what a test about the
                // AL-37 lock-out expiring needs, since its shortest legal duration is 30 seconds.
                if (clock is not null)
                {
                    builder.Services.AddSingleton(clock);
                }

                // The verifier under test is the real one — issuer, audience, expiry and
                // signature are all checked for real. Only where the *provider's* keys come from
                // is faked, because the alternative is a test that reaches accounts.google.com.
                builder.Services.AddSingleton<MageRide.Iam.Auth.IOidcKeySource>(oidc);
            });

        await app.StartAsync();

        var baseAddress = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };

        return new IamHarness(app, client, sms, oidc, baseAddress, postgres.ConnectionString);
    }

    /// <summary>
    /// POSTs JSON with an <c>Idempotency-Key</c>. A fresh key unless the caller supplies one —
    /// D3' §0 makes the header mandatory, so omitting it by accident would test the 400 path.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? idempotencyKey = null, string? bearer = null)
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

    /// <summary>POSTs without the header, for the tests that assert it is required.</summary>
    public Task<HttpResponseMessage> PostWithoutKeyAsync(string path, object? body) =>
        Client.PostAsJsonAsync(path, body ?? new { });

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Requests an OTP and returns the <c>authId</c> and the code the sender received.</summary>
    public async Task<(string AuthId, string Code)> RequestOtpAsync(string phone, string deviceId, string app = "passenger")
    {
        var response = await PostAsync("/v1/auth/otp/request", new { phone, deviceId, role = app });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        return (body.GetProperty("authId").GetString()!, Sms.LastCodeFor(phone));
    }

    /// <summary>The whole OTP round trip, as an app performs it.</summary>
    public async Task<SignedIn> SignInAsync(string phone, string deviceId, string app = "passenger")
    {
        var (authId, code) = await RequestOtpAsync(phone, deviceId, app);

        var response = await PostAsync("/v1/auth/otp/verify", new { authId, otp = code, deviceId });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return SignedIn.From(await ReadJsonAsync(response));
    }

    /// <summary>GETs with a bearer token.</summary>
    public Task<HttpResponseMessage> GetAsync(string path, string? bearer = null) =>
        SendAsync(HttpMethod.Get, path, null, bearer);

    /// <summary>PUTs JSON with a bearer token. No <c>Idempotency-Key</c> — D3' requires it on POST only.</summary>
    public Task<HttpResponseMessage> PutAsync(string path, object? body, string? bearer = null) =>
        SendAsync(HttpMethod.Put, path, body, bearer);

    public Task<HttpResponseMessage> DeleteAsync(string path, string? bearer = null) =>
        SendAsync(HttpMethod.Delete, path, null, bearer);

    /// <summary>GETs a service-to-service route with the shared secret ride-svc would carry.</summary>
    public Task<HttpResponseMessage> GetInternalAsync(string path, string? apiKey = InternalApiKey, string? caller = "ride-svc")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (apiKey is not null)
        {
            request.Headers.Add(Iam.Endpoints.UserLookupEndpoints.ApiKeyHeader, apiKey);
        }

        if (caller is not null)
        {
            request.Headers.Add(Iam.Endpoints.UserLookupEndpoints.CallerHeader, caller);
        }

        return Client.SendAsync(request);
    }

    /// <summary>
    /// Signs a provisioned portal account in and returns its access token — the only way to hold a
    /// token for one of the six internal roles or for a fleet owner, since no portal sign-in
    /// creates an account (AL-06, AL-03).
    /// </summary>
    public async Task<string> PortalTokenAsync(string email, string password)
    {
        var response = await PostFromBrowserAsync("/v1/auth/password", new { email, password });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        return body.GetProperty("accessToken").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? bearer)
    {
        var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return Client.SendAsync(request);
    }

    /// <summary>POSTs a portal sign-in body from a browser — no <c>X-Platform</c>, a user agent.</summary>
    public Task<HttpResponseMessage> PostFromBrowserAsync(
        string path, object? body, string? userAgent = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("User-Agent", userAgent ?? "Mozilla/5.0 (MageRide portal tests)");

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Oidc.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>The body of a successful <c>POST /v1/auth/otp/verify</c>.</summary>
internal sealed record SignedIn(
    string AccessToken, string RefreshToken, int ExpiresIn, string UserId, string Role, bool IsNewUser)
{
    public static SignedIn From(JsonElement body)
    {
        var user = body.GetProperty("user");

        return new SignedIn(
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!,
            body.GetProperty("expiresIn").GetInt32(),
            user.GetProperty("userId").GetString()!,
            user.GetProperty("role").GetString()!,
            body.GetProperty("isNewUser").GetBoolean());
    }
}
