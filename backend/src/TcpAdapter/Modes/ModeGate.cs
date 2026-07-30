using MageRide.Shared.Caching;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Observability;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.TcpAdapter.Modes;

/// <summary>Whether a tracker's samples may be published, and under what metadata.</summary>
/// <param name="Publishable">Whether the sample reaches EMQX.</param>
/// <param name="Profile">The vehicle's mode, type and fleet — stamped onto the sample.</param>
/// <param name="Reason">Why it was refused, for the metric label and the log line.</param>
public sealed record ModeVerdict(bool Publishable, VehicleProfile? Profile, string? Reason = null);

/// <summary>T-11's routing rule, applied at ingest (§7.7.7, D6' §4.5).</summary>
public interface IModeGate
{
    /// <summary>Decides whether this vehicle's tracker may publish right now.</summary>
    Task<ModeVerdict> EvaluateAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IModeGate"/>
/// <remarks>
/// <para>
/// <b>The rule.</b> §7.7.7, read literally:
/// </para>
/// <list type="bullet">
/// <item><b>Mode A</b> (public bus, fleet): "no driver-app session is required for position to
/// publish; the tracker is the authoritative and only source". Accepted always.</item>
/// <item><b>Mode B</b> (private, school buses, book-hires): "like Mode A for tracker-installed
/// vehicles — the journey auto-starts/ends on ignition, no app required" (US-3.23). Accepted always.</item>
/// <item><b>Mode C</b> (ride-hailing): "tracker GPS for a Mode C vehicle is ingested <b>only while the
/// vehicle is Online</b> (the driver has gone online in the app) — pings sent while offline are
/// rejected and never reach the live map or dispatch".</item>
/// </list>
/// <para>
/// <b>"Online" is <c>veh:driver:{vehicleId}</c>.</b> dispatch-svc writes that key at the one moment a
/// (driver, vehicle) pair is established — <c>POST /v1/standby/online</c> — and deletes it when the
/// driver goes off duty, which is precisely the sentence §7.7.7 writes. It is also already the binding
/// position-processor-svc resolves a driver through, so the two planes read one fact rather than each
/// deriving "online" its own way. The availability hash's <c>state</c> is deliberately <b>not</b>
/// consulted: a driver mid-offer or mid-ride is not AVAILABLE and is emphatically online, and gating on
/// the phase would take a vehicle off the map the moment it was hired.
/// </para>
/// <para>
/// <b>Refusal happens here, at ingest, and not downstream.</b> §7.7.7's "never reach the live map or
/// dispatch" is a statement about where the sample stops: publishing it and filtering later would put
/// it on <c>telemetry.raw</c>, through position-processor's Redis writes and onto the cell streams
/// fanout-svc reads. position-processor-svc's own notes say the same thing from the other side — its
/// C039 handoff lists T-11 as not its gate.
/// </para>
/// <para>
/// <b>A Mode C refusal is not an error and is not logged per sample.</b> A three-wheeler with a
/// tracker reports all night; the counter is the record, and a log line per ping would be the loudest
/// thing in the deployment.
/// </para>
/// </remarks>
public sealed class ModeGate(
    VehicleProfileCache profiles,
    IConnectionMultiplexer redis,
    IOptions<AdapterOptions> options,
    ILogger<ModeGate> logger) : IModeGate
{
    /// <summary>Metric label: a Mode C vehicle whose driver is not online.</summary>
    public const string ReasonModeCOffline = "mode_c_offline";

    /// <summary>Metric label: the vehicle's mode could not be resolved at all.</summary>
    public const string ReasonModeUnknown = "mode_unknown";

    /// <summary>Metric label: <c>registry.vehicles</c> has no such row.</summary>
    public const string ReasonNoVehicle = "no_vehicle";

    private readonly AdapterOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<ModeVerdict> EvaluateAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(vehicleId, cancellationToken);

        if (profile is null)
        {
            // Two different situations reach this and they are distinguished by whether the lookup
            // answered: a vehicle the registry does not have (a binding pointing at a deleted row) and
            // a lookup that could not run. Neither can be told apart here, so both take the
            // configured direction — see Adapter:PublishWhenModeUnknown, which argues why it is open.
            AdapterDiagnostics.SamplesGated.Add(
                1, AdapterDiagnostics.Tag("reason", ReasonModeUnknown));

            return new ModeVerdict(
                _options.PublishWhenModeUnknown, null, _options.PublishWhenModeUnknown ? null : ReasonNoVehicle);
        }

        if (!profile.IsModeC)
        {
            return new ModeVerdict(true, profile);
        }

        var online = await IsDriverOnlineAsync(vehicleId);

        if (online)
        {
            return new ModeVerdict(true, profile);
        }

        AdapterDiagnostics.SamplesGated.Add(1, AdapterDiagnostics.Tag("reason", ReasonModeCOffline));

        return new ModeVerdict(false, profile, ReasonModeCOffline);
    }

    private async Task<bool> IsDriverOnlineAsync(Guid vehicleId)
    {
        try
        {
            var bound = await redis.GetDatabase().StringGetAsync(RedisKeys.VehicleDriver(vehicleId));

            return !bound.IsNullOrEmpty
                   && Guid.TryParse(bound.ToString(), out var driverId)
                   && driverId != Guid.Empty;
        }
        catch (RedisException exception)
        {
            // Redis down. Treated as online, which is the same direction as the unknown-mode case and
            // for the same reason: the alternative is that a cache outage silently takes every Mode C
            // tracker on the platform off the map, and dispatch still applies its own freshness gate
            // to anything this admits.
            logger.LogError(
                exception,
                "Could not read the standby binding for vehicle {VehicleId}; admitting the sample and " +
                "leaving the T-11 gate open for this one", vehicleId);

            return true;
        }
    }
}
