using System.Globalization;
using Dapper;
using MageRide.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.Ride.Tests.Infrastructure;

/// <summary>
/// iam-svc's <c>GET /v1/users/lookup</c>, on a real socket (P-03).
/// </summary>
/// <remarks>
/// <para>
/// A stub rather than the real service, because iam-svc's answer is one row of one table and
/// hosting it would drag its signing keys, its SMS gateway and its own migrations into every ride
/// test. The <b>question</b> it answers is real, though: this reads the same <c>iam.users</c> the
/// harness seeds, so a rider created by <c>CreatePassengerAsync</c> is registered and a number
/// nobody has ever used is not — the two cases the P-03 branch turns on, without a test having to
/// arrange either.
/// </para>
/// <para>
/// It is also the only way to exercise the shape that matters on the failure side: a stub that can
/// be switched off is how "iam-svc is down" becomes an asserted <c>503 dependency-unavailable</c>
/// rather than a comment.
/// </para>
/// </remarks>
internal sealed class IamLookupStub : IAsyncDisposable
{
    /// <summary>Shared with the route handler, which is mapped before the stub object exists.</summary>
    private sealed class State
    {
        public int Lookups;

        public volatile bool Offline;
    }

    private readonly WebApplication _app;
    private readonly State _state;

    private IamLookupStub(WebApplication app, State state, string baseAddress)
    {
        _app = app;
        _state = state;
        BaseAddress = baseAddress;
    }

    /// <summary>Where <c>Ride:IamBaseUrl</c> points.</summary>
    public string BaseAddress { get; }

    /// <summary>How many lookups ride-svc has made — the registration oracle's own audit, in miniature.</summary>
    public int Lookups => Volatile.Read(ref _state.Lookups);

    /// <summary>Answers 503 to everything, so the caller's outage path can be asserted.</summary>
    public bool Offline
    {
        get => _state.Offline;
        set => _state.Offline = value;
    }

    public static async Task<IamLookupStub> StartAsync(PostgresFixture postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var state = new State();
        var app = builder.Build();

        app.MapGet("/v1/users/lookup", async (string? phone) =>
        {
            Interlocked.Increment(ref state.Lookups);

            if (state.Offline)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(phone))
            {
                return Results.BadRequest();
            }

            await using var connection = await postgres.OpenAsync();

            var userId = await connection.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM iam.users WHERE phone = @Phone;", new { Phone = phone });

            // The two answers the contract publishes, and no PII beyond them.
            return userId is { } id
                ? Results.Ok(new { registered = true, userId = id.ToString() })
                : Results.Ok(new { registered = false });
        });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        return new IamLookupStub(app, state, address);
    }

    /// <summary>A number in the shape <c>_shared.yaml#PhoneE164</c> publishes, belonging to nobody.</summary>
    public static string UnregisteredPhone() =>
        "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
