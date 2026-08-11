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
    // Container 6 exposes NO ports — the spec's table says "None (internal consumers only)". These
    // are loopback-only so `/health/ready` is still reachable from inside the container, which is
    // what the compose healthcheck curls; nothing outside can route to them.
    firstPort: 5200,
    bindAddress: "127.0.0.1",
    args);
