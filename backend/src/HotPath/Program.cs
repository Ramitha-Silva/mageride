using MageRide.FleetHealth;
using MageRide.HotPath.Host;
using MageRide.HotPath.MqttBridge;
using MageRide.HotPath.PersistenceWriter;
using MageRide.HotPath.PositionProcessor;

// =====================================================================================
// Container 6 of the lightweight production replica: four logical services, one process.
//
// The order below is the order they start in, and it is the order the spec's data flow runs:
// mqtt-bridge feeds `telemetry.raw`, position-processor consumes it and produces
// `telemetry.normalized`, persistence-writer consumes that, and fleet-health aggregates the status
// plane beside them. Consumers are started before producers so nothing published during start-up
// waits on a consumer group that does not exist yet — with `AutoOffsetReset.Latest` on
// position-processor (its CLAUDE.md explains why it is the one service reading Latest) a sample
// published into a topic nobody is subscribed to is a sample nobody ever sees.
// =====================================================================================
var services = new CoLocatedService[]
{
    new("persistence-writer-svc", PersistenceWriterApplication.Build),
    new("position-processor-svc", PositionProcessorApplication.Build),
    new("fleet-health-svc", FleetHealthApplication.Build),
    new("mqtt-bridge-svc", MqttBridgeApplication.Build),
};

return await CoLocatedHost.RunAsync(
    "hot-path",
    services,
    // Container 6 publishes NO HOST ports — the spec's table says "None (internal consumers only)"
    // and the compose file still declares no `ports:`. Loopback is right for the three consumers:
    // `/health/ready` stays reachable from inside the container, which is what the healthcheck
    // curls, and nothing outside can route to them.
    firstPort: 5200,
    bindAddress: "127.0.0.1",
    args,
    // Δ C126 — EXCEPT fleet-health-svc, which is not a consumer: it serves
    // `GET /v1/fleets/{fleetId}/health` (US-3.13, C044) and the gateway reaches it from ANOTHER
    // container. gateway-routes.json has named `http://hot-path:5000/` all along and says why in
    // as many words; binding it to 127.0.0.1 made that address unreachable, so the route answered
    // 503 on every deployment until C126's live contract sweep asked it. 5000 rather than its
    // positional 5202 so that the gateway's default is simply correct and the replica needs no
    // override — one number in one place instead of the three different ones that were in flight.
    published: new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fleet-health-svc"] = "http://0.0.0.0:5000",
    });
