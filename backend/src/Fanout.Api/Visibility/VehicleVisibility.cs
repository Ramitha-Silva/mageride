using MageRide.Shared.Realtime;

namespace MageRide.Fanout.Visibility;

/// <summary>The operating modes a frame can carry (D5' §2, <c>OperatingMode</c>).</summary>
public static class OperatingModes
{
    /// <summary>Public transport — bus and train. Always visible (D6' §5.2).</summary>
    public const string A = "A";

    /// <summary>A private shared vehicle. Visible only to an entitled passenger (D-23).</summary>
    public const string B = "B";

    /// <summary>On-demand. Visible while idle, hidden while on hire (US-7.16).</summary>
    public const string C = "C";
}

/// <summary>Where one vehicle's position may go.</summary>
public enum VehicleAudience
{
    /// <summary>The public geocell group — Mode A always, idle Mode C.</summary>
    Public,

    /// <summary>Only <c>vehicle:{vehicleId}</c>: the entitled Mode B watchers, and the own driver.</summary>
    Entitled,

    /// <summary>Only <c>ride:{rideId}</c>: a Mode C vehicle on active hire (US-7.16).</summary>
    Ride,

    /// <summary>Nobody. Stale, offline, or a mode this service does not recognise.</summary>
    None,
}

/// <summary>
/// What the D-22/D-23 filter decided about one frame, and why.
/// </summary>
/// <param name="Audience">Where it may go.</param>
/// <param name="RideId">The hire, when <paramref name="Audience"/> is <see cref="VehicleAudience.Ride"/>.</param>
/// <param name="RemovalReason">
/// The <c>VehicleRemoved</c> reason to publish if this vehicle was on the public map a moment ago,
/// or <see langword="null"/> when it never belonged there. A Mode B vehicle is not <em>removed</em>
/// from the public map — it was never on it.
/// </param>
public sealed record VehicleVerdict(VehicleAudience Audience, Guid? RideId = null, string? RemovalReason = null)
{
    public static readonly VehicleVerdict Public = new(VehicleAudience.Public);

    public static readonly VehicleVerdict Private = new(VehicleAudience.Entitled);

    /// <summary>
    /// A frame whose mode is missing or not one of the three. Nothing is <em>removed</em>, because
    /// nothing was ever published: a frame that cannot be classified has never been on a group.
    /// </summary>
    public static readonly VehicleVerdict Unclassified = new(VehicleAudience.None);

    public static readonly VehicleVerdict Stale =
        new(VehicleAudience.None, RemovalReason: VehicleRemovalReasons.Stale);

    public static readonly VehicleVerdict Offline =
        new(VehicleAudience.None, RemovalReason: VehicleRemovalReasons.Offline);

    public static VehicleVerdict Engaged(Guid rideId) =>
        new(VehicleAudience.Ride, rideId, VehicleRemovalReasons.Engaged);

    /// <summary>
    /// The metric dimension — <c>engaged</c>, <c>stale</c>, <c>offline</c>, <c>private</c> or
    /// <c>unclassified</c>.
    /// </summary>
    public string FilterReason => RemovalReason
        ?? (Audience == VehicleAudience.Entitled ? "private" : "unclassified");
}

/// <summary>
/// What this service knows about a vehicle that is not in the frame itself: whether it is on hire,
/// and when its broker session last died.
/// </summary>
/// <param name="EngagedOn">The active hire, or <see langword="null"/> (<c>veh:engaged:{vehicleId}</c>).</param>
/// <param name="OfflineAt">When the last will last fired, or <see langword="null"/> (<c>veh:offline:{vehicleId}</c>).</param>
public sealed record VehicleState(Guid? EngagedOn, DateTimeOffset? OfflineAt)
{
    /// <summary>A vehicle nothing is known about: idle, and never seen to go offline.</summary>
    public static readonly VehicleState Unknown = new(null, null);
}

/// <summary>
/// The D-22/D-23/US-7.16/US-7.17 visibility rules, as one pure function.
/// </summary>
/// <remarks>
/// <para>
/// <b>The filter splits in two, and only one half is per passenger.</b> Whether a vehicle is stale,
/// offline or on hire is a fact about the <em>vehicle</em> and is identical for every subscriber, so
/// it is decided once per frame here and the surviving frames go to the cell group as one batch —
/// ADD §7.4's O(updates × subscribers-per-cell) cost model is untouched. Whether a passenger may see
/// a Mode B vehicle is a fact about the <em>pair</em>, and D6' §5.2 settles it at group join rather
/// than per frame: an entitled passenger is a member of <c>vehicle:{vehicleId}</c> and everyone else
/// is not, so no frame is ever tested against a passenger at all.
/// </para>
/// <para>
/// <b>An unknown mode is nobody's.</b> The mode is denormalised onto the sample by the publisher
/// (<c>mqtt-topics.md</c> §2.1) and a frame that arrives without one cannot be classified; showing
/// it would mean publishing a vehicle whose visibility rule is unknown, which is the one failure
/// this filter exists to prevent. It is dropped and counted.
/// </para>
/// </remarks>
public static class VehicleVisibilityRules
{
    /// <summary>
    /// Decides where <paramref name="mode"/>'s frame may go.
    /// </summary>
    /// <param name="mode">The frame's <c>mode</c> field — <c>A</c>, <c>B</c> or <c>C</c>.</param>
    /// <param name="sampleTs">
    /// The sample's GNSS instant, or <see langword="null"/> when the entry carried none. A frame
    /// with no timestamp is treated as current: position-processor-svc stamps every entry it writes,
    /// so the only way to reach here without one is a hand-written stream, and refusing those would
    /// make the filter depend on a field the wire contract marks optional.
    /// </param>
    /// <param name="state">What <c>veh:engaged</c> and <c>veh:offline</c> say about the vehicle.</param>
    /// <param name="now">The clock.</param>
    /// <param name="freshnessWindow">US-7.17's window.</param>
    public static VehicleVerdict Classify(
        string? mode, DateTimeOffset? sampleTs, VehicleState state, DateTimeOffset now, TimeSpan freshnessWindow)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Staleness and the last will are checked before the mode, because they are the two rules
        // that apply to every mode alike (US-7.17) — and because a vehicle that is not ingesting has
        // no current position to classify in the first place.
        if (sampleTs is { } stamped && now - stamped > freshnessWindow)
        {
            return VehicleVerdict.Stale;
        }

        // The last will beats the sample only when it is the later of the two. A device whose
        // session died and then reconnected publishing may never send an `online` — the fresher
        // sample is what says it is back, and comparing instants rather than reading a flag is what
        // makes that self-healing.
        if (state.OfflineAt is { } offlineAt && (sampleTs is not { } ts || offlineAt >= ts))
        {
            return VehicleVerdict.Offline;
        }

        return mode switch
        {
            // Buses and trains are public infrastructure; there is no entitlement to check and
            // nothing to hide behind.
            OperatingModes.A => VehicleVerdict.Public,

            // Never on a public group, whatever else is true of it (D-23).
            OperatingModes.B => VehicleVerdict.Private,

            OperatingModes.C => state.EngagedOn is { } rideId
                ? VehicleVerdict.Engaged(rideId)
                : VehicleVerdict.Public,

            _ => VehicleVerdict.Unclassified,
        };
    }
}
