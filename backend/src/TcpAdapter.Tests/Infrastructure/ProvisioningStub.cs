using System.Collections.Concurrent;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Endpoints;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>
/// provisioning-svc's two internal tracker routes, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a stub and not the real service.</b> What the adapter needs from C030 on this path is four
/// fields — <c>{valid, vehicleId, state}</c> and a 204 — and running the real thing to produce them
/// would drag in step-ca, that service's migrations and an authenticated bind flow. What the stub
/// cannot do is prove the two agree about a *format*, so the two formats where a divergence would be
/// silent are asserted against Provisioning.Api's own types instead
/// (<see cref="Identity.CredentialTests"/>).
/// </para>
/// <para>
/// It does hold C030's real constants: the header name, the query parameter and the binding-state
/// strings all come from that project, so a rename there fails this suite rather than quietly
/// producing a stub that answers a question nobody asks any more.
/// </para>
/// </remarks>
internal sealed class ProvisioningStub : IAsyncDisposable
{
    /// <summary>The shared secret. Must match what the adapter is configured with.</summary>
    public const string ApiKey = "c043-provisioning-internal-key";

    private readonly WebApplication _app;

    private ProvisioningStub(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>Where the adapter should point <c>Adapter:ProvisioningBaseUrl</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>The bindings this stub knows about, keyed by IMEI.</summary>
    public ConcurrentDictionary<string, StubBinding> Bindings { get; } = new(StringComparer.Ordinal);

    /// <summary>IMEIs reported through <c>POST /{imei}/quarantine</c> — the T-08 evidence.</summary>
    public ConcurrentBag<string> Quarantined { get; } = [];

    /// <summary>How many times <c>validate</c> was called, so a test can prove the cache was used.</summary>
    public int ValidateCalls => Volatile.Read(ref _validateCalls);

    private int _validateCalls;

    /// <summary>Starts the stub on an ephemeral loopback port.</summary>
    public static async Task<ProvisioningStub> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        ProvisioningStub? stub = null;

        app.MapGet("/v1/internal/trackers/{imei}/validate", (string imei, HttpContext context) =>
        {
            if (!Authorised(context))
            {
                return Results.Unauthorized();
            }

            Interlocked.Increment(ref stub!._validateCalls);

            // A serial is optional on the real route too, and passing one is what forces C030 past its
            // own cache into Postgres so the anti-clone rule can see it.
            _ = context.Request.Query[InternalTrackerEndpoints.CredentialSerialQuery].ToString();

            // Always 200, as C030 does: an unknown IMEI is a verdict, not an error, and a 404 would put
            // "never heard of it" and "this service is misrouted" in one bucket on the hot path.
            if (!stub.Bindings.TryGetValue(imei, out var binding))
            {
                return Results.Ok(new { valid = false, vehicleId = (string?)null, state = (string?)null });
            }

            return Results.Ok(new
            {
                valid = binding.State == BindingStates.Active,
                vehicleId = binding.VehicleId.ToString(),
                state = binding.State,
            });
        });

        app.MapPost("/v1/internal/trackers/{imei}/quarantine", (string imei, HttpContext context) =>
        {
            if (!Authorised(context))
            {
                return Results.Unauthorized();
            }

            stub!.Quarantined.Add(imei);

            // 204 whether or not there was an ACTIVE binding to hold: the adapter reports the same
            // clone on every reconnect and a second report is not an error.
            return Results.NoContent();
        });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        stub = new ProvisioningStub(app, address);
        return stub;

        static bool Authorised(HttpContext context) =>
            context.Request.Headers[InternalTrackerEndpoints.ApiKeyHeader].ToString() == ApiKey;
    }

    /// <summary>Binds an IMEI to a vehicle in the ACTIVE state.</summary>
    public void Bind(string imei, Guid vehicleId) =>
        Bindings[imei] = new StubBinding(vehicleId, BindingStates.Active);

    /// <summary>Moves a binding to REVOKED — what a decommission, an unbind or a revoke all leave.</summary>
    public void Revoke(string imei) =>
        Bindings[imei] = new StubBinding(
            Bindings.TryGetValue(imei, out var existing) ? existing.VehicleId : Guid.NewGuid(),
            BindingStates.Revoked);

    /// <summary>Holds both records the way T-08 does.</summary>
    public void Quarantine(string imei) =>
        Bindings[imei] = new StubBinding(
            Bindings.TryGetValue(imei, out var existing) ? existing.VehicleId : Guid.NewGuid(),
            BindingStates.Quarantined);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>One row of <c>prov.tracker_bindings</c>, as far as the adapter can see it.</summary>
    internal sealed record StubBinding(Guid VehicleId, string State);
}
