using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Iam.Otp;
using MageRide.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private static int _phoneCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _app;

    private IamHarness(WebApplication app, HttpClient client, CapturingOtpSender sms, string baseAddress)
    {
        _app = app;
        Client = client;
        Sms = sms;
        BaseAddress = baseAddress;
    }

    public HttpClient Client { get; }

    public CapturingOtpSender Sms { get; }

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
        PostgresFixture postgres, RedisFixture redis, IDictionary<string, string?>? settings = null)
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

                // Registered before AddIamServices, whose sender registration is a TryAdd.
                builder.Services.AddSingleton<IOtpSender>(sms);
            });

        await app.StartAsync();

        var baseAddress = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };

        return new IamHarness(app, client, sms, baseAddress);
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

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
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
