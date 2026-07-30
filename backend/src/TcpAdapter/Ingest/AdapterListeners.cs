using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Protocols;
using Microsoft.Extensions.Logging;

namespace MageRide.TcpAdapter.Ingest;

/// <summary>
/// Every listener this pod runs, built once and handed to the host as hosted services.
/// </summary>
/// <remarks>
/// <para>
/// A single object rather than four registrations, for a reason worth writing down:
/// <c>AddHostedService&lt;T&gt;</c> registers through <c>TryAddEnumerable</c>, which de-duplicates by
/// <i>implementation type</i> — so registering <see cref="TrackerListener"/> three times, once per TCP
/// family, silently keeps only the first and two protocol ports never open. Building them here and
/// exposing each as its own <c>IHostedService</c> descriptor sidesteps that entirely.
/// </para>
/// <para>
/// It is also how a test asks which port a listener actually bound, which matters because the suite
/// binds port 0 and lets the OS choose — three fixed ports would make the suite unrunnable beside a
/// dev stack that already holds 5023-5026.
/// </para>
/// </remarks>
public sealed class AdapterListeners
{
    private readonly Dictionary<ProtocolFamily, object> _listeners = [];

    public AdapterListeners(SessionServices services, SocketBudget budget, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(loggers);

        var options = services.Options;

        if (!options.Enabled)
        {
            return;
        }

        var ports = options.ResolvePorts();

        foreach (var family in ProtocolFamilies.All)
        {
            if (!options.IsEnabled(family))
            {
                continue;
            }

            _listeners[family] = ProtocolFamilies.Transport(family) == ProtocolTransport.Udp
                ? new NmeaUdpListener(ports[family], services, loggers)
                : new TrackerListener(family, ports[family], services, budget, loggers);
        }
    }

    /// <summary>Every listener, in family order, as hosted services.</summary>
    public IEnumerable<BackgroundService> All =>
        ProtocolFamilies.All.Where(_listeners.ContainsKey).Select(family => (BackgroundService)_listeners[family]);

    /// <summary>
    /// The port a family's listener bound, or null when this pod does not serve it.
    /// </summary>
    /// <remarks>
    /// Null until the listener has actually bound, which is why a test waits on it rather than reading
    /// it straight after <c>StartAsync</c>: a <see cref="BackgroundService"/>'s <c>ExecuteAsync</c> is
    /// started, not awaited, by the host.
    /// </remarks>
    public int? PortFor(ProtocolFamily family) => _listeners.TryGetValue(family, out var listener)
        ? listener switch
        {
            TrackerListener tcp => tcp.BoundPort == 0 ? null : tcp.BoundPort,
            NmeaUdpListener udp => udp.BoundPort == 0 ? null : udp.BoundPort,
            _ => null,
        }
        : null;
}
