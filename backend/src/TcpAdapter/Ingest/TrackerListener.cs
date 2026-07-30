using System.Net;
using System.Net.Sockets;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Observability;
using MageRide.TcpAdapter.Protocols;

// `System.Net.Sockets` has a ProtocolFamily of its own (the address family of a socket). This
// file needs both namespaces, so the spec's word wins by name.
using ProtocolFamily = MageRide.TcpAdapter.Protocols.ProtocolFamily;

namespace MageRide.TcpAdapter.Ingest;

/// <summary>
/// One protocol family's TCP listener — 5023 GT06, 5024 JT/T 808, 5025 H02 (ADD §7.7.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>A raw socket, not Kestrel.</b> These are not HTTP connections and there is nothing for a web
/// server to contribute: no request framing, no headers, no middleware pipeline. A device holds one
/// socket for hours (HAProxy's tracker frontends set <c>timeout client 4h</c>) and speaks a
/// length-prefixed binary protocol, so the whole of the transport is accept, read, frame, write — and
/// <c>Socket</c>'s async operations are already the <c>SocketAsyncEventArgs</c> pool the manifest's
/// deliverable line names, wrapped in the API that does not leak the pooling into every call site.
/// </para>
/// <para>
/// <b>The port is L4 passthrough and never HTTP-routed</b> — this component's third fence.
/// <c>infra/deploy/haproxy.cfg</c> puts 5023-5025 in <c>mode tcp</c> frontends with no inspection, and
/// there is deliberately no path here by which a request could be routed anywhere.
/// </para>
/// <para>
/// <b>The accept loop never awaits a session.</b> Each accepted socket is handed to a session that runs
/// on its own task; awaiting one would serialise the whole family behind whichever device is quietest.
/// Sessions are tracked so the host's shutdown can drain them rather than dropping ten thousand
/// sockets at once — a mass reset is precisely the reconnect storm R-09 exists to prevent.
/// </para>
/// </remarks>
public sealed class TrackerListener : BackgroundService
{
    private readonly ProtocolFamily _family;
    private readonly int _port;
    private readonly SessionServices _services;
    private readonly SocketBudget _budget;
    private readonly AdapterOptions _options;
    private readonly ILogger _logger;
    private readonly List<Task> _sessions = [];
    private readonly Lock _gate = new();

    public TrackerListener(
        ProtocolFamily family,
        int port,
        SessionServices services,
        SocketBudget budget,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggers);

        _family = family;
        _port = port;
        _services = services;
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _options = services.Options;
        _logger = loggers.CreateLogger($"MageRide.TcpAdapter.{ProtocolFamilies.Name(family)}.listener");
    }

    /// <summary>The port actually bound. Differs from the configured one when a test asks for 0.</summary>
    public int BoundPort { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var address = IPAddress.Parse(_options.BindAddress);
        using var listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        // No SO_REUSEADDR: two processes sharing a tracker port would each get a random half of a
        // fleet's sockets, and the per-device state this service holds (see SessionRegistry) makes
        // that silently wrong rather than merely surprising.
        listener.Bind(new IPEndPoint(address, _port));
        listener.Listen(_options.Backlog);

        BoundPort = ((IPEndPoint)listener.LocalEndPoint!).Port;

        _logger.LogInformation(
            "{Adapter} listening on {Address}:{Port} (socket budget {Budget} for this pod)",
            ProtocolFamilies.Name(_family), _options.BindAddress, BoundPort, _budget.Ceiling);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket accepted;

                try
                {
                    accepted = await listener.AcceptAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException exception)
                {
                    // A connection that died between the SYN and the accept. Not fatal to the listener.
                    _logger.LogDebug(exception, "{Adapter} accept failed", ProtocolFamilies.Name(_family));
                    continue;
                }

                if (!_budget.TryAcquire())
                {
                    AdapterDiagnostics.SocketsRefused.Add(
                        1,
                        AdapterDiagnostics.Tag("family", ProtocolFamilies.Name(_family)),
                        AdapterDiagnostics.Tag("reason", "budget"));

                    _logger.LogWarning(
                        "Refusing a {Adapter} connection from {Peer}: this pod holds its {Budget}-socket budget",
                        ProtocolFamilies.Name(_family), accepted.RemoteEndPoint, _budget.Ceiling);

                    Close(accepted);
                    continue;
                }

                AdapterDiagnostics.SocketsAccepted.Add(
                    1, AdapterDiagnostics.Tag("family", ProtocolFamilies.Name(_family)));

                Track(ServeAsync(accepted, stoppingToken));
            }
        }
        finally
        {
            await DrainAsync();
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken stoppingToken)
    {
        // Nagle off: a GT06 acknowledgement is five bytes and a device that does not get it inside its
        // own timeout re-sends its login and eventually reboots its modem. Coalescing a five-byte
        // write with the next one costs exactly the delay that matters here.
        try
        {
            socket.NoDelay = true;
        }
        catch (SocketException)
        {
            // Not supported on this platform's socket; harmless.
        }

        var session = new TrackerSession(socket, _family, _services);

        try
        {
            await session.RunAsync(stoppingToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "A {Adapter} session ended unexpectedly", ProtocolFamilies.Name(_family));
        }
        finally
        {
            _budget.Release();
        }
    }

    private void Track(Task session)
    {
        lock (_gate)
        {
            _sessions.RemoveAll(task => task.IsCompleted);
            _sessions.Add(session);
        }
    }

    private async Task DrainAsync()
    {
        Task[] pending;

        lock (_gate)
        {
            pending = [.. _sessions];
        }

        if (pending.Length == 0)
        {
            return;
        }

        _logger.LogInformation(
            "{Adapter} draining {Count} session(s)", ProtocolFamilies.Name(_family), pending.Length);

        // Every session is already cancelled by the stopping token; this waits for their teardowns,
        // which is what publishes the retained status=offline for each (T-04). Bounded: a rollout
        // must not hang on one socket whose broker publish is stuck.
        try
        {
            await Task.WhenAll(pending).WaitAsync(_options.OfflineWindow + TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is TimeoutException or AggregateException)
        {
            _logger.LogWarning("{Adapter} stopped with sessions still draining", ProtocolFamilies.Name(_family));
        }
    }

    private static void Close(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // Already gone.
        }
        finally
        {
            socket.Dispose();
        }
    }
}
