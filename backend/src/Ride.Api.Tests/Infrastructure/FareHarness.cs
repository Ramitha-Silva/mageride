using System.Net.Http.Headers;
using System.Text.Json;
using MageRide.Fare;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MageRide.Ride.Tests.Infrastructure;

/// <summary>
/// A running fare-svc stub on a real socket.
/// </summary>
/// <remarks>
/// It shares <see cref="RideHarness.FareTokenKey"/> and <paramref name="tokens"/> with the ride
/// harness, because the only thing worth testing across the two services is that the token one
/// mints is the token the other accepts.
/// <para>
/// <b>Δ C049: fare-svc now has a database.</b> The C022 stub priced from a hard-coded table and
/// declared no Postgres; the real service resolves <c>fares.tariffs</c> by <c>effective_from</c> and
/// evaluates <c>fares.peak_windows</c>, so it is pointed at the same migrated fixture ride-svc uses
/// and prices from 1901's seeded rate card.
/// </para>
/// </remarks>
internal sealed class FareHarness : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FareHarness(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<FareHarness> StartAsync(TestTokenIssuer tokens, PostgresFixture postgres)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(postgres);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Fare:EstimateTokenKey"] = RideHarness.FareTokenKey,
            ["Otel:PrometheusEnabled"] = "false",
        };

        var app = FareApplication.Build(
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
                builder.WebHost.UseUrls("http://127.0.0.1:0");

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

        return new FareHarness(app, new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(30) });
    }

    public Task<HttpResponseMessage> EstimateAsync(
        string bearer,
        double fromLat = 6.9344,
        double fromLng = 79.8428,
        double toLat = 6.8514,
        double toLng = 79.8653,
        string vehicleType = "three_wheeler",
        string? kind = null)
    {
        var query =
            $"?fromLat={fromLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&fromLng={fromLng.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&toLat={toLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&toLng={toLng.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&vehicleType={vehicleType}" +
            (kind is null ? string.Empty : $"&kind={kind}");

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/fare/estimate" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return Client.SendAsync(request);
    }

    public static Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) => RideHarness.ReadJsonAsync(response);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
