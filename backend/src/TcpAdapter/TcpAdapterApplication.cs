using MageRide.Shared.Caching;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Identity;
using MageRide.TcpAdapter.Ingest;
using MageRide.TcpAdapter.Modes;
using MageRide.TcpAdapter.Observability;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.TcpAdapter;

/// <summary>
/// Composition root for tcp-adapter. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>A plain host, not a web application.</b> <c>mqtt-topics.md</c> §7: "tcp-adapter is one
/// StatefulSet per protocol family and has <b>no HTTP surface</b>", and D7' §5.1 gives it a
/// TCP-socket liveness probe (<c>nc -z 127.0.0.1 5023</c>) rather than <c>/health/live</c> for exactly
/// that reason. So there is no <c>AddMageRideDefaults</c> here: that call configures the HTTP kernel —
/// RFC 7807 errors, the <c>Idempotency-Key</c> middleware, the health endpoints, JWT bearer — and none
/// of it has anything to configure in a process no request can arrive at. The pieces that are not HTTP
/// are registered individually below, and each is the same kernel call every other service makes.
/// </para>
/// <para>
/// <b>What "ready" means without a readiness endpoint.</b> The liveness signal is the listening socket,
/// which exists once <see cref="TrackerListener"/> has bound. A device that connects while EMQX is
/// unreachable authenticates normally and its samples are dropped by
/// <see cref="EmqxLink.PublishAsync"/> — counted, and visible as
/// <c>mageride.tracker.samples_gated{reason=broker_unavailable}</c>. That is the right shape here:
/// refusing devices because a downstream is briefly away would turn a broker restart into a fleet-wide
/// reconnect storm (R-09), and what is lost in the window is a few seconds of positions the device
/// supersedes anyway.
/// </para>
/// </remarks>
public static class TcpAdapterApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the MQTT principal.</summary>
    public const string ServiceName = "tcp-adapter";

    /// <summary>Builds the host. <paramref name="configure"/> runs before anything is registered.</summary>
    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args);

        configure?.Invoke(builder);

        var services = builder.Services;
        var configuration = builder.Configuration;

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<AdapterOptions>()
            .Bind(configuration.GetSection(AdapterOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var adapter = configuration.GetSection(AdapterOptions.SectionName).Get<AdapterOptions>() ?? new AdapterOptions();

        // The settings object itself, so SessionServices and AdapterListeners can be constructed by
        // the container rather than by a factory that lists every dependency twice.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<AdapterOptions>>().Value);

        // The kernel pieces this process actually uses. Postgres is here because T-11 needs the
        // vehicle's mode and the canonical sample needs its type — see Modes/VehicleProfiles.cs, which
        // argues the micro-change-set against D7' §2.1's container table.
        services.AddMageRideTelemetry(configuration, ServiceName);
        services.AddMageRideRedis(configuration);
        services.PostConfigure<RedisOptions>(redis => redis.ClientName ??= ServiceName);
        services.AddMageRidePostgres(configuration);
        services.PostConfigure<PostgresOptions>(postgres => postgres.ApplicationName ??= ServiceName);
        services.AddMageRideMqtt(configuration);

        services.AddSingleton<PskCredentials>();
        services.AddSingleton<IProtocolCodecFactory, ProtocolCodecFactory>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<SocketBudget>();
        services.AddSingleton<IVehicleProfileRepository, VehicleProfileRepository>();
        services.AddSingleton<VehicleProfileCache>();
        services.AddSingleton<IModeGate, ModeGate>();
        services.AddSingleton<EmqxLink>();
        services.AddSingleton<ITrackerPublisher, TrackerPublisher>();
        services.AddSingleton<DownlinkRouter>();
        services.AddSingleton<RevocationWatcher>();
        services.AddSingleton<SessionServices>();
        services.AddSingleton<AdapterListeners>();

        AddInternalClient<ITrackerDirectory, TrackerDirectory>(
            services, TrackerDirectory.HttpClientName, adapter.ProvisioningBaseUrl, adapter.ProvisioningTimeout);

        AddInternalClient<IIgnitionReporter, TripStateIgnitionReporter>(
            services, TripStateIgnitionReporter.HttpClientName, adapter.TripStateBaseUrl, adapter.TripStateTimeout);

        services.AddHostedService(provider => provider.GetRequiredService<EmqxLink>());
        services.AddHostedService(provider => provider.GetRequiredService<RevocationWatcher>());

        // Each listener as its own descriptor. AddHostedService would de-duplicate the three TCP ones
        // by implementation type and open one port instead of three — see AdapterListeners.
        services.AddSingleton<IHostedService>(provider =>
            new ListenerHost(provider.GetRequiredService<AdapterListeners>()));

        var host = builder.Build();

        // Wired before the link starts, so the filter is in place on the first CONNECT rather than
        // waiting for a reconnect.
        host.Services.GetRequiredService<DownlinkRouter>().Attach();

        var budget = host.Services.GetRequiredService<SocketBudget>();

        MageRideDiagnostics.Meter.CreateObservableGauge(
            AdapterDiagnostics.OpenSocketsGauge,
            () => budget.Open,
            "{socket}",
            "Device sockets open on this pod (ADD §7.7.6's per-pod budget).");

        WarnAboutSilentMisconfiguration(host, adapter);

        return host;
    }

    /// <summary>
    /// Says, once, everything that is switched off in a way a device cannot tell you about.
    /// </summary>
    /// <remarks>
    /// Every item here has the same shape: the adapter keeps running, nothing errors, and the symptom
    /// appears somewhere else entirely — a fleet that never starts a journey, a revoked tracker that
    /// keeps publishing, a Mode C vehicle on the public map. That is precisely the class of fault a
    /// start-up line is for.
    /// </remarks>
    private static void WarnAboutSilentMisconfiguration(IHost host, AdapterOptions adapter)
    {
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(adapter.ProvisioningBaseUrl))
        {
            // The one that refuses every device. C030's fence: "an adapter that cannot reach validate
            // refuses every device, which is the safe way round" — and completely silent from the
            // device's side, which is why it is said here.
            logger.LogError(
                "Adapter:ProvisioningBaseUrl is not configured. Every device whose imei cache entry is " +
                "absent will be refused at connect (T-01, T-03), because there is nothing to ask. Set it " +
                "to provisioning-svc's base address.");
        }
        else if (string.IsNullOrWhiteSpace(adapter.ProvisioningInternalApiKey))
        {
            logger.LogError(
                "Adapter:ProvisioningInternalApiKey is not configured. provisioning-svc does not map " +
                "/v1/internal/trackers/** without its own key either, so validate answers 404 and every " +
                "cache miss becomes a refused device.");
        }

        if (string.IsNullOrWhiteSpace(adapter.PskKeyDirectory))
        {
            logger.LogWarning(
                "Adapter:PskKeyDirectory is not configured, so a presented PSK credential cannot be " +
                "verified locally (D6' §4.2). Devices still resolve through validate — which answers " +
                "revocation but not forgery.");
        }

        if (string.IsNullOrWhiteSpace(adapter.TripStateBaseUrl))
        {
            logger.LogWarning(
                "Adapter:TripStateBaseUrl is not configured: ACC transitions are decoded and not " +
                "reported, so tracker-equipped Mode A/B vehicles will not auto-start or auto-end their " +
                "sessions on ignition (AL-32, US-3.22/3.23).");
        }

        if (adapter.PublishWhenModeUnknown)
        {
            logger.LogInformation(
                "Adapter:PublishWhenModeUnknown is on: a vehicle whose registry profile cannot be read " +
                "publishes anyway, so T-11's Mode C gate is open for it. The alternative takes every " +
                "Mode A bus off the map on a Postgres blip.");
        }

        if (!adapter.PublishPresence)
        {
            logger.LogWarning(
                "Adapter:PublishPresence is off: a half-closed socket publishes no retained " +
                "status=offline, so trip-state-svc, dispatch-svc and fleet-health-svc never learn a " +
                "tracker went away (T-04, R-15).");
        }

        if (!adapter.DownlinkEnabled)
        {
            logger.LogWarning(
                "Adapter:DownlinkEnabled is off: veh/+/cmd is not subscribed and no command reaches a " +
                "device on this pod (ADD §7.7.5).");
        }
    }

    /// <summary>
    /// A named client for one <c>/v1/internal/**</c> hop.
    /// </summary>
    /// <remarks>
    /// The base address is only set when it is configured: an unset one leaves the client relative-only,
    /// which fails fast on the first call rather than at start-up — and both callers check the setting
    /// themselves and do nothing, so the failure never happens in a deployment that meant to be without
    /// it. The service is a singleton and resolves a client per call through the factory, so the handler
    /// still rotates.
    /// </remarks>
    private static void AddInternalClient<TService, TImplementation>(
        IServiceCollection services, string name, string? baseUrl, TimeSpan timeout)
        where TService : class
        where TImplementation : class, TService
    {
        services.AddHttpClient(name, http =>
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                http.BaseAddress = new Uri(baseUrl);
            }

            // Belt and braces with the per-call CancelAfter: a handler with no timeout at all leaves a
            // hung TCP connect holding a device's connect path for the OS default.
            http.Timeout = timeout + TimeSpan.FromSeconds(1);
        });

        services.AddSingleton<TService, TImplementation>();
    }
}

/// <summary>
/// Starts and stops every listener as one hosted service.
/// </summary>
/// <remarks>
/// The listeners are <see cref="BackgroundService"/>s built by <see cref="AdapterListeners"/>; this is
/// what puts them under the host's lifetime without going through <c>AddHostedService&lt;T&gt;</c>,
/// whose <c>TryAddEnumerable</c> would drop the second and third TCP listener as duplicates of the
/// first.
/// </remarks>
internal sealed class ListenerHost(AdapterListeners listeners) : IHostedService
{
    private readonly List<BackgroundService> _started = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var listener in listeners.All)
        {
            await listener.StartAsync(cancellationToken);
            _started.Add(listener);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Reverse order, so the accept loops stop before the sessions they own are drained.
        for (var index = _started.Count - 1; index >= 0; index--)
        {
            try
            {
                await _started[index].StopAsync(cancellationToken);
            }
            catch (Exception)
            {
                // A listener that failed to bind has nothing to stop; the host is going away regardless.
            }

            _started[index].Dispose();
        }

        _started.Clear();
    }
}
