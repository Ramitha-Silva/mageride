using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.HotPath.Host;

/// <summary>One logical service inside a co-located container, and the method that builds it.</summary>
/// <param name="Name">
/// The service's own <c>ServiceName</c> — the name it reports to telemetry, uses as its Postgres
/// application name and as its Kafka client id. Repeated here only so this host's own log lines and
/// failure messages can name which of the co-located services did something.
/// </param>
/// <param name="Build">
/// The service's real entry point. Every one of the platform's 29 services exposes the same
/// <c>Build(WebApplicationOptions, Action&lt;WebApplicationBuilder&gt;?)</c>, which is what makes
/// co-location a hosting decision rather than a code change — the spec's Container 7 note says
/// exactly that: "extracting to individual containers for production is a configuration change, not
/// a code change".
/// </param>
public sealed record CoLocatedService(
    string Name,
    Func<WebApplicationOptions, Action<WebApplicationBuilder>?, WebApplication> Build);

/// <summary>
/// Starts several of the platform's services in one process, as the lightweight production replica's
/// Containers 6 and 7 require.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every service keeps its own pipeline.</b> Each gets its own <see cref="WebApplication"/>, its
/// own DI container, its own configuration binding and its own hosted services — this type starts
/// them and stops them and does nothing else. Two services registering the same singleton, the same
/// Kafka consumer group or the same metrics exporter cannot collide, because nothing is shared but
/// the process, the thread pool and the GC heap. That sharing is the whole cost of the replica's
/// layout and the spec's Container 6 warning is explicit about it.
/// </para>
/// <para>
/// <b>Ports are deterministic, not OS-assigned.</b> The E2E fleets bind <c>127.0.0.1:0</c> and read
/// the address back, which is right for a test that owns both ends. Here the gateway has to be told
/// where each service is before any of them starts, and a container healthcheck has to name a port
/// in a Dockerfile, so the ports are assigned from <c>firstPort</c> in list order and are stable
/// across restarts.
/// </para>
/// <para>
/// <b>Start order is the list's order; shutdown is its reverse.</b> Nothing is torn down under an
/// in-flight call from a service still running, which is the same rule the E2E fleets' DisposeAsync
/// follows.
/// </para>
/// </remarks>
public static class CoLocatedHost
{
    /// <summary>
    /// Builds and starts every service, then blocks until the process is asked to stop.
    /// </summary>
    /// <param name="container">The replica container's name, for this host's own log lines.</param>
    /// <param name="services">The services to co-locate, in start order.</param>
    /// <param name="firstPort">The port the first service binds; each subsequent one takes the next.</param>
    /// <param name="bindAddress">
    /// <c>127.0.0.1</c> for a container that exposes nothing, <c>0.0.0.0</c> for the one port the
    /// container publishes.
    /// </param>
    /// <param name="args">The process arguments, handed to every service's configuration.</param>
    /// <returns>0 on a clean shutdown, 1 if any service failed to start.</returns>
    public static async Task<int> RunAsync(
        string container,
        IReadOnlyList<CoLocatedService> services,
        int firstPort,
        string bindAddress,
        string[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        var started = new List<WebApplication>(services.Count);
        var addresses = Addresses(services, firstPort, bindAddress);

        try
        {
            for (var index = 0; index < services.Count; index++)
            {
                var service = services[index];
                var url = addresses[service.Name];

                var application = service.Build(
                    new WebApplicationOptions
                    {
                        Args = args,
                        ApplicationName = service.Name,
                    },
                    builder =>
                    {
                        // The service's own configuration cannot be allowed to choose the port: the
                        // gateway was told this address before the service existed, and two services
                        // reading the same ASPNETCORE_URLS out of the shared environment would both
                        // try to bind it.
                        builder.WebHost.UseUrls(url);
                        Configure?.Invoke(service.Name, builder);
                    });

                await application.StartAsync().ConfigureAwait(false);
                started.Add(application);

                application.Services
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("MageRide.Replica")
                    .LogInformation(
                        "{Container}: {Service} listening on {Url}", container, service.Name, url);
            }
        }
        catch (Exception failure)
        {
            // A container whose fourth service failed to start is not a container that is 75% up: the
            // spec's data flow needs all of them, and compose would report the container healthy on
            // the strength of whichever health endpoint the healthcheck happens to name. Say which
            // one failed and exit non-zero so the restart policy sees it.
            await Console.Error.WriteLineAsync(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: {1} of {2} services started; the next one failed: {3}",
                    container,
                    started.Count,
                    services.Count,
                    failure)).ConfigureAwait(false);

            await StopAllAsync(started).ConfigureAwait(false);
            return 1;
        }

        // Any one of them shutting down takes the container with it — an outcome the restart policy
        // can act on, unlike a process that keeps running with a dead consumer inside it.
        var shutdowns = started.Select(a => a.WaitForShutdownAsync()).ToArray();
        await Task.WhenAny(shutdowns).ConfigureAwait(false);
        await StopAllAsync(started).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Set by a host that needs to configure each service as it is built.</summary>
    /// <remarks>
    /// Container 7 uses this to hand the gateway the loopback addresses of the services it fronts.
    /// Container 6 has nothing to add.
    /// </remarks>
    public static Action<string, WebApplicationBuilder>? Configure { get; set; }

    /// <summary>The address each service will listen on, assigned before any of them starts.</summary>
    /// <remarks>
    /// Public because Container 7's host needs the same map to point the gateway's clusters at, and
    /// computing it twice from the same rule is how the two would drift.
    /// </remarks>
    public static Dictionary<string, string> Addresses(
        IReadOnlyList<CoLocatedService> services, int firstPort, string bindAddress)
    {
        ArgumentNullException.ThrowIfNull(services);

        var map = new Dictionary<string, string>(services.Count, StringComparer.Ordinal);

        for (var index = 0; index < services.Count; index++)
        {
            map[services[index].Name] = string.Format(
                CultureInfo.InvariantCulture,
                "http://{0}:{1}",
                bindAddress,
                firstPort + index);
        }

        return map;
    }

    /// <summary>Stops in reverse start order, and lets a failed stop not prevent the others.</summary>
    private static async Task StopAllAsync(List<WebApplication> started)
    {
        for (var index = started.Count - 1; index >= 0; index--)
        {
            try
            {
                await started[index].StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                await started[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                await Console.Error.WriteLineAsync($"shutdown: {failure.Message}").ConfigureAwait(false);
            }
        }
    }
}
