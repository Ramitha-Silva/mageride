using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
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

namespace MageRide.Registry.Tests.Infrastructure;

/// <summary>
/// A running registry-svc on a real socket, against a real Postgres.
/// </summary>
/// <remarks>
/// Built through <see cref="RegistryApplication.Build"/>, so the pipeline under test —
/// deny-by-default authorization, the idempotency middleware, the problem+json handler — is
/// the one the process runs. Kestrel rather than TestServer for the same reason C008's and
/// C020's harnesses use it: the idempotency middleware swaps the response body feature.
/// </remarks>
internal sealed class RegistryHarness : IAsyncDisposable
{
    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private RegistryHarness(WebApplication app, HttpClient client, TestTokenIssuer tokens, PostgresFixture postgres)
    {
        _app = app;
        _postgres = postgres;
        Client = client;
        Tokens = tokens;
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-QA-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static Task<RegistryHarness> StartAsync(
        PostgresFixture postgres, IDictionary<string, string?>? settings = null) =>
        StartAsync(postgres, Environments.Development, settings);

    public static async Task<RegistryHarness> StartAsync(
        PostgresFixture postgres, string environmentName, IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var tokens = new TestTokenIssuer();

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

        var app = RegistryApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = environmentName,
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

        return new RegistryHarness(app, client, tokens, postgres);
    }

    /// <summary>
    /// Creates the <c>iam.users</c> row a vehicle's <c>owner_id</c> foreign key needs, and
    /// returns a driver bearer for it. registry-svc never creates accounts — iam-svc does.
    /// </summary>
    public async Task<Guid> CreateDriverAsync()
    {
        var driverId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'driver');",
            new { Id = driverId, Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture) });

        return driverId;
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>
    /// POSTs JSON with an <c>Idempotency-Key</c>. A fresh key unless the caller supplies one —
    /// D3' §0 makes the header mandatory, so omitting it by accident would test the 400 path.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? bearer, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        Authorize(request, bearer);

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(request, bearer);
        return Client.SendAsync(request);
    }

    /// <summary>Registers a vehicle and returns the 201 body, failing the test on anything else.</summary>
    public async Task<JsonElement> RegisterVehicleAsync(
        string bearer, string? plate = null, string vehicleType = "three_wheeler", string driverName = "Test Driver")
    {
        var response = await PostAsync(
            "/v1/vehicles",
            new { registrationNumber = plate ?? NextPlate(), vehicleType, mode = "C", driverName },
            bearer);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    /// <summary>Registers a vehicle and approves it through the dev seed path.</summary>
    public async Task<string> RegisterApprovedVehicleAsync(
        string bearer, string? plate = null, string vehicleType = "three_wheeler")
    {
        var vehicleId = (await RegisterVehicleAsync(bearer, plate, vehicleType)).GetProperty("vehicleId").GetString()!;

        var approved = await PostAsync($"/v1/dev/vehicles/{vehicleId}/approve", null, bearer);
        Assert.Equal(System.Net.HttpStatusCode.OK, approved.StatusCode);

        return vehicleId;
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static void Authorize(HttpRequestMessage request, string? bearer)
    {
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }
    }
}
