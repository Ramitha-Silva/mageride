using MageRide.Query.Configuration;
using MageRide.Query.Persistence;
using MageRide.Shared.Observability;
using MageRide.Shared.Primitives;
using MageRide.Shared.Realtime;
using Microsoft.Extensions.Options;

namespace MageRide.Query.Live;

/// <summary>One vehicle as the live map draws it.</summary>
/// <param name="VehicleId">The vehicle.</param>
/// <param name="Type">Canonical vehicle type (AL-09).</param>
/// <param name="Mode">A, B or C.</param>
/// <param name="Point">Where it is.</param>
/// <param name="HeadingDeg">MAP-06's direction arrow, or <see langword="null"/> if not reported.</param>
/// <param name="SpeedMps">Speed over ground, or <see langword="null"/>.</param>
/// <param name="DriverName">Mode C, post-accept, caller's own ride only (US-7.12).</param>
/// <param name="RegistrationNumber">Mode A/B popup (US-7.4), or the caller's own vehicle (US-7.12).</param>
/// <param name="EtaSeconds">US-7.11's estimate, or <see langword="null"/>.</param>
public sealed record NearbyVehicleView(
    Guid VehicleId,
    string Type,
    string Mode,
    GeoPoint Point,
    int? HeadingDeg,
    double? SpeedMps,
    string? DriverName,
    string? RegistrationNumber,
    int? EtaSeconds);

/// <summary>A whole snapshot.</summary>
/// <param name="Vehicles">What the caller may see.</param>
/// <param name="AsOf">When it was taken.</param>
/// <param name="LimitedLive">ADD §12's degradation flag — the live index could not be read.</param>
public sealed record NearbySnapshot(
    IReadOnlyList<NearbyVehicleView> Vehicles, DateTimeOffset AsOf, bool LimitedLive)
{
    public static NearbySnapshot Degraded(DateTimeOffset asOf) => new([], asOf, LimitedLive: true);
}

/// <summary>What a caller asked for.</summary>
/// <param name="ViewerId">Whose map it is — two of the four visibility rules need it.</param>
/// <param name="Centre">Where the map is centred.</param>
/// <param name="RadiusM">How far out to look, already validated and clamped.</param>
/// <param name="Types">Canonical types to include; empty means all, trains included (US-7.7).</param>
/// <param name="Modes">Modes to include; empty means all.</param>
public sealed record NearbyQuery(
    Guid ViewerId,
    GeoPoint Centre,
    int RadiusM,
    IReadOnlySet<string> Types,
    IReadOnlySet<string> Modes);

/// <summary>The live-map snapshot read (D3' <c>GET /v1/nearby</c>).</summary>
public interface INearbyService
{
    Task<NearbySnapshot> SearchAsync(NearbyQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// The same read for a named set of vehicles rather than a circle — US-7.9's buses on a route.
    /// </summary>
    Task<NearbySnapshot> SnapshotAsync(
        Guid viewerId, IReadOnlyCollection<Guid> vehicleIds, GeoPoint? etaTarget, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="INearbyService" path="/summary"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>The four rules are not implemented here.</b> They are
/// <see cref="VehicleVisibilityRules.Classify"/> in the kernel, which fanout-svc applies to every
/// frame it pushes to a socket. <c>signalr-hub.md</c> §1.1 makes this endpoint the snapshot and resync
/// path for that same map, so the two must agree exactly — and the only way to guarantee that is for
/// there to be one function. What is added here is the part a request-shaped read has to do for
/// itself and a group-based one does not: testing <c>share:{userId}</c> per caller, because there is
/// no group join to have settled it.
/// </para>
/// <para>
/// <b>Where this deliberately differs from fanout, and why it is not a leak.</b> An engaged Mode C
/// vehicle is excluded from the public answer — and included for the passenger whose ride engaged it,
/// which is the second half of US-7.16 ("only the booking passenger sees the assigned vehicle").
/// Membership is decided by <c>rides.rides</c>: the engagement key names the ride, and the database
/// says whether the caller is a party to it. Nothing here re-derives which states count as engaged —
/// the key's presence already answered that, and a second copy of ride-svc's state machine is how the
/// two would drift.
/// </para>
/// <para>
/// <b>A vehicle whose mode cannot be determined is not drawn, even though this service could look it
/// up.</b> The registry knows every vehicle's mode, so a Postgres read would classify a sample whose
/// publisher omitted the field — and fanout-svc, which holds no database, drops that frame. Taking
/// the more generous path would put a marker on the map that the socket then never moves: a frozen
/// vehicle a passenger walks towards. The two planes fail the same way on purpose.
/// </para>
/// <para>
/// <b>The registry is read only for vehicles whose registration may be disclosed.</b> US-7.4 gives
/// the details popup to Mode A and Mode B only and US-7.12 gives the plate and the driver's name to
/// the accepted ride alone, so an idle Mode C taxi's identity is never fetched at all. The privacy
/// rule is the data-access shape rather than a field-stripping step that could be forgotten.
/// </para>
/// </remarks>
public sealed class NearbyService(
    ILiveVehicleIndex index,
    ILiveReadRepository repository,
    EtaEstimator eta,
    IOptions<QueryOptions> options,
    TimeProvider clock,
    ILogger<NearbyService> logger) : INearbyService
{
    /// <summary>An empty filter set means "every value" — see <see cref="Matches"/>.</summary>
    private static readonly IReadOnlySet<string> NoFilter = new HashSet<string>(StringComparer.Ordinal);

    private readonly QueryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<NearbySnapshot> SearchAsync(NearbyQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = await index.SearchAsync(query.Centre, query.RadiusM, cancellationToken);

        if (candidates.LimitedLive)
        {
            return NearbySnapshot.Degraded(clock.GetUtcNow());
        }

        return await BuildAsync(query.ViewerId, candidates.Vehicles, query.Types, query.Modes, query.Centre, cancellationToken);
    }

    public async Task<NearbySnapshot> SnapshotAsync(
        Guid viewerId,
        IReadOnlyCollection<Guid> vehicleIds,
        GeoPoint? etaTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);

        var resolved = await index.ReadAsync(vehicleIds, cancellationToken);

        return await BuildAsync(
            viewerId,
            [.. resolved.Values],
            NoFilter,
            NoFilter,
            etaTarget,
            cancellationToken);
    }

    private async Task<NearbySnapshot> BuildAsync(
        Guid viewerId,
        IReadOnlyList<LiveVehicle> candidates,
        IReadOnlySet<string> types,
        IReadOnlySet<string> modes,
        GeoPoint? etaTarget,
        CancellationToken cancellationToken)
    {
        var asOf = clock.GetUtcNow();

        if (candidates.Count == 0)
        {
            return new NearbySnapshot([], asOf, LimitedLive: false);
        }

        // A vehicle whose publisher denormalised neither field is dropped before anything is looked
        // up. Both are required by the contract and each for its own reason: MAP-03 draws a marker
        // *by* type (colour and rail icon both), and without a mode there is no visibility rule to
        // apply — which is the one failure the filter exists to prevent, and exactly what fanout-svc
        // does with the same frame. Counted under fanout's own `unclassified` reason so the two
        // planes' numbers stay comparable.
        var describable = new List<LiveVehicle>(candidates.Count);
        var unclassified = 0;

        foreach (var vehicle in candidates)
        {
            if (vehicle.Type is { Length: > 0 } && vehicle.Mode is { Length: > 0 })
            {
                describable.Add(vehicle);
            }
            else
            {
                unclassified++;
            }
        }

        if (unclassified > 0)
        {
            MageRideDiagnostics.NearbyVehiclesFiltered.Add(
                unclassified, new KeyValuePair<string, object?>("reason", "unclassified"));
        }

        // US-7.7's filter is the cheapest predicate on the page, so it runs before the engagement and
        // entitlement reads: a passenger who asked for trains has no interest in the state of forty
        // taxis.
        var wanted = describable
            .Where(vehicle => Matches(types, vehicle.Type) && Matches(modes, vehicle.Mode))
            .ToArray();

        if (wanted.Length == 0)
        {
            return new NearbySnapshot([], asOf, LimitedLive: false);
        }

        var ids = wanted.Select(static vehicle => vehicle.VehicleId).ToArray();

        var states = _options.VisibilityEnabled
            ? await index.ReadStateAsync(ids, cancellationToken)
            : new Dictionary<Guid, VehicleState>();

        var verdicts = new List<(LiveVehicle Vehicle, VehicleVerdict Verdict)>(wanted.Length);

        foreach (var vehicle in wanted)
        {
            var verdict = _options.VisibilityEnabled
                ? VehicleVisibilityRules.Classify(
                    vehicle.Mode,
                    vehicle.SampleTs,
                    states.TryGetValue(vehicle.VehicleId, out var state) ? state : VehicleState.Unknown,
                    asOf,
                    _options.FreshnessWindow)
                : VehicleVerdict.Public;

            verdicts.Add((vehicle, verdict));
        }

        var entitled = await ResolveEntitlementsAsync(viewerId, verdicts, cancellationToken);
        var ownRides = await ResolveOwnRidesAsync(viewerId, verdicts, cancellationToken);

        // Kept in two passes so the registry read is one query over the vehicles that survived,
        // rather than one per vehicle inside the loop that decides which those are.
        var visible = new List<(LiveVehicle Vehicle, OwnRide? Ride)>(verdicts.Count);

        foreach (var (vehicle, verdict) in verdicts)
        {
            switch (verdict.Audience)
            {
                case VehicleAudience.Public:
                    visible.Add((vehicle, null));
                    break;

                case VehicleAudience.Entitled when !_options.EntitlementEnabled
                                                   || entitled.Contains(vehicle.VehicleId):
                    visible.Add((vehicle, null));
                    break;

                case VehicleAudience.Ride when verdict.RideId is { } rideId
                                               && ownRides.TryGetValue(rideId, out var ride):
                    visible.Add((vehicle, ride));
                    break;

                default:
                    MageRideDiagnostics.NearbyVehiclesFiltered.Add(
                        1, new KeyValuePair<string, object?>("reason", verdict.FilterReason));
                    break;
            }
        }

        if (visible.Count == 0)
        {
            return new NearbySnapshot([], asOf, LimitedLive: false);
        }

        var identities = await ReadDisclosableIdentitiesAsync(visible, cancellationToken);

        var views = new List<NearbyVehicleView>(visible.Count);

        foreach (var (vehicle, ride) in visible)
        {
            identities.TryGetValue(vehicle.VehicleId, out var identity);

            views.Add(new NearbyVehicleView(
                vehicle.VehicleId,
                // `veh:meta` carries the type the publisher denormalised; the registry row, when it
                // was read at all, is the authoritative copy of the same fact.
                identity?.VehicleType ?? vehicle.Type!,
                identity?.Mode ?? vehicle.Mode!,
                vehicle.Point,
                vehicle.HeadingDeg,
                vehicle.SpeedMps,
                // US-7.12: only ever the caller's own accepted vehicle.
                ride is null ? null : identity?.DriverName,
                identity?.RegistrationNumber,
                EstimateEta(vehicle, ride, etaTarget)));
        }

        MageRideDiagnostics.NearbyVehiclesReturned.Add(views.Count);

        return new NearbySnapshot(views, asOf, LimitedLive: false);
    }

    /// <summary>
    /// US-7.11 has two halves and they point at different places.
    /// </summary>
    /// <remarks>
    /// "Valid only for the accepted vehicle" — for that one, the estimate is to whichever end of the
    /// journey is still ahead: the pickup until the passenger is aboard, the drop-off after. "However,
    /// buses (Mode A) can also display ETA when selected on the map" — for those, the only target the
    /// request supplies is the passenger's own map centre, which is also the only one they care about:
    /// when the bus reaches them. Mode B and idle Mode C get none, because neither is going anywhere
    /// the caller has named.
    /// </remarks>
    private int? EstimateEta(LiveVehicle vehicle, OwnRide? ride, GeoPoint? centre)
    {
        if (ride is not null)
        {
            var target = ride.State is RideStates.InProgress ? ride.Dropoff : ride.Pickup;
            return eta.Estimate(vehicle, target);
        }

        return vehicle.Mode == OperatingModes.A && centre is { } point
            ? eta.Estimate(vehicle, point)
            : null;
    }

    private async Task<ISet<Guid>> ResolveEntitlementsAsync(
        Guid viewerId,
        IReadOnlyList<(LiveVehicle Vehicle, VehicleVerdict Verdict)> verdicts,
        CancellationToken cancellationToken)
    {
        if (!_options.EntitlementEnabled)
        {
            return new HashSet<Guid>();
        }

        var privateVehicles = verdicts
            .Where(static entry => entry.Verdict.Audience is VehicleAudience.Entitled)
            .Select(static entry => entry.Vehicle.VehicleId)
            .ToArray();

        return privateVehicles.Length == 0
            ? new HashSet<Guid>()
            : await index.ReadEntitlementsAsync(viewerId, privateVehicles, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Guid, OwnRide>> ResolveOwnRidesAsync(
        Guid viewerId,
        IReadOnlyList<(LiveVehicle Vehicle, VehicleVerdict Verdict)> verdicts,
        CancellationToken cancellationToken)
    {
        if (!_options.OwnRideEnabled)
        {
            return new Dictionary<Guid, OwnRide>();
        }

        var rideIds = verdicts
            .Where(static entry => entry.Verdict.Audience is VehicleAudience.Ride)
            .Select(static entry => entry.Verdict.RideId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();

        return rideIds.Length == 0
            ? new Dictionary<Guid, OwnRide>()
            : await repository.ReadOwnRidesAsync(viewerId, rideIds, cancellationToken);
    }

    /// <summary>
    /// Reads registry identity for the vehicles whose identity the caller is allowed to be told.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, VehicleIdentity>> ReadDisclosableIdentitiesAsync(
        IReadOnlyList<(LiveVehicle Vehicle, OwnRide? Ride)> visible, CancellationToken cancellationToken)
    {
        var disclosable = visible
            .Where(static entry =>
                // The Mode A/B details popup (US-7.4, MAP-07) …
                entry.Vehicle.Mode is OperatingModes.A or OperatingModes.B
                // … and the caller's own accepted Mode C vehicle (US-7.12). An idle Mode C taxi is
                // neither, and US-7.4 says so explicitly: "Standby on-demand vehicles do not show
                // info when tapped."
                || entry.Ride is not null)
            .Select(static entry => entry.Vehicle.VehicleId)
            .ToArray();

        if (disclosable.Length == 0)
        {
            return new Dictionary<Guid, VehicleIdentity>();
        }

        try
        {
            return await repository.ReadIdentitiesAsync(disclosable, cancellationToken);
        }
        catch (Npgsql.NpgsqlException failure)
        {
            // The positions are the map; the plate is the popup. A registry outage should cost the
            // popup, not the screen — the alternative is a passenger who cannot see any bus because
            // the database that knows its number plate is down.
            logger.LogError(
                failure, "registry.vehicles is unreachable; serving the snapshot without plates or names.");

            return new Dictionary<Guid, VehicleIdentity>();
        }
    }

    private static bool Matches(IReadOnlySet<string> filter, string? value) =>
        filter.Count == 0 || (value is not null && filter.Contains(value));
}

/// <summary>
/// The two <c>rides.rides.state</c> values this service names, and nothing more.
/// </summary>
/// <remarks>
/// Deliberately not a copy of ride-svc's eighteen. Which states count as <em>engaged</em> is settled
/// by <c>veh:engaged:{vehicleId}</c> existing, which fanout-svc maintains; the only thing left to
/// know is whether the journey has started, so that an ETA points at the drop-off rather than the
/// pickup. A state this service has never heard of therefore reads as "not yet aboard", which is the
/// safe direction: an ETA to the pickup for a passenger already in the car is wrong by a few minutes,
/// where the reverse would tell somebody waiting on a pavement that their car is 20 minutes away in
/// the opposite direction.
/// </remarks>
internal static class RideStates
{
    internal const string InProgress = "InProgress";
}
