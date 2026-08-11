using MageRide.AppServices;
using MageRide.HotPath.Host;
using Microsoft.Extensions.Configuration;

// =====================================================================================
// Container 7 of the lightweight production replica: the 22 domain services plus the YARP gateway,
// one process, one published port. What lives here is Container7's list; this file starts it.
// =====================================================================================
var addresses = CoLocatedHost.Addresses(
    Container7.Services, Container7.FirstServicePort, "127.0.0.1");

// The gateway starts last, so every cluster it fronts is already listening.
//
// It is in this container by default because that is the replica's Container 7 — "YARP gateway + 21
// domain services". `infra/docker-compose.dev.yml` runs the gateway as its OWN container beside this
// one, and two gateways both binding 5000 in one process is a start-up failure, so that file sets
// AppServices__IncludeGateway=false. The switch exists so one image serves both shapes; the default
// follows the spec.
var includeGateway = !string.Equals(
    Environment.GetEnvironmentVariable("AppServices__IncludeGateway"),
    "false",
    StringComparison.OrdinalIgnoreCase);

var all = includeGateway
    ? Container7.Services.Append(Container7.Gateway).ToArray()
    : Container7.Services.ToArray();

CoLocatedHost.Configure = (name, builder) =>
{
    if (!string.Equals(name, Container7.Gateway.Name, StringComparison.Ordinal))
    {
        return;
    }

    // The one routable address in this container: the spec's Container 7 table publishes 5000 and
    // HAProxy's backend points at it. CoLocatedHost already called UseUrls with a loopback address
    // from the list position; the gateway is the exception.
    builder.WebHost.UseUrls($"http://0.0.0.0:{Container7.GatewayPort}");

    // Point every co-located cluster at the loopback port its service is listening on.
    //
    // Added inside `configure`, which runs after GatewayApplication.Build adds gateway-routes.json,
    // and the last configuration source wins. An environment variable would also work since Δ C125
    // re-added the environment above that file — but it would mean the compose file restating what
    // this host already knows, and the two drifting.
    //
    // Container7.ClustersElsewhere is absent from this map on purpose and keeps the address the
    // environment gives it.
    builder.Configuration.AddInMemoryCollection(
        addresses.ToDictionary(
            entry => $"ReverseProxy:Clusters:{entry.Key}:Destinations:primary:Address",
            entry => (string?)(entry.Value + "/"),
            StringComparer.Ordinal));
};

return await CoLocatedHost.RunAsync(
    "app-services",
    all,
    firstPort: Container7.FirstServicePort,
    bindAddress: "127.0.0.1",
    args);
