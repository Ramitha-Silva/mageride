namespace MageRide.Reputation.Domain;

/// <summary>
/// The four values <c>reputation.block_states.state</c> may hold (server_db_schema.md §7, D-04).
/// </summary>
/// <remarks>
/// Strings rather than an enum because that is what is stored, what the CHECK constraint names and
/// what the contract's <c>BlockStatus</c> enum prints. <see cref="Severity"/> is the only ordering
/// there is: nothing in D5' says WARN is "half" of BOOKING_DISABLED, only that a caller gating on
/// the state excludes the last two (D5' §3.2).
/// </remarks>
public static class BlockStates
{
    public const string Ok = "OK";

    /// <summary>
    /// One step short of a hard threshold. <b>No spec pins when WARN is entered</b> — the enum
    /// exists in D4'/server_db_schema.md §7 and no rule in D5' produces it, so the thresholds are
    /// configuration (<c>Reputation:CancellationWarnThreshold</c> and friends) and the default is
    /// "one short of the block". Recorded in the C033 handoff.
    /// </summary>
    public const string Warn = "WARN";

    /// <summary>3 consecutive post-acceptance cancellations (US-6A.10b, AL-16, D5' §7.2).</summary>
    public const string BookingDisabled = "BOOKING_DISABLED";

    /// <summary>3 confirmed reports (US-12.6), or the brief delist a driver-side cancel earns (§11.12).</summary>
    public const string Delisted = "DELISTED";

    public static readonly IReadOnlyList<string> All = [Ok, Warn, BookingDisabled, Delisted];

    /// <summary>
    /// How far a state is from OK. Used to decide whether a recompute may replace a state that is
    /// still serving its time box — a driver who earned a 30-minute delist does not get it lifted
    /// by the next recompute that happens to find their counters merely at WARN.
    /// </summary>
    public static int Severity(string? state) => state switch
    {
        Delisted => 3,
        BookingDisabled => 2,
        Warn => 1,
        _ => 0,
    };

    public static bool IsKnown(string? state) => state is not null && All.Contains(state);

    /// <summary>
    /// The D5' §3.2 hard gate: "exclude driver if <c>block_state ∈ {BOOKING_DISABLED, DELISTED}</c>".
    /// Precomputed once here so dispatch-svc, fanout-svc and the admin UI cannot each spell it
    /// slightly differently.
    /// </summary>
    public static bool AllowsDispatch(string? state) =>
        state is not (BookingDisabled or Delisted);
}

/// <summary>Which side of a ride a fact is about. The rules differ per side.</summary>
public static class SubjectRoles
{
    public const string Passenger = "passenger";
    public const string Driver = "driver";

    public static bool IsKnown(string? role) => role is Passenger or Driver;
}

/// <summary>
/// Why a block state holds. Written to <c>reputation.block_states.reason</c> and read back when
/// the state's time box expires, to decide what has been served and may be forgiven.
/// </summary>
public static class BlockReasons
{
    /// <summary>3 consecutive post-acceptance cancellations (D5' §7.2).</summary>
    public const string CancellationsDisabled = "cancellations_disabled";

    /// <summary>3 confirmed reports inside the window (US-12.6, D5' §4.2).</summary>
    public const string ReportsDelist = "reports_delist";

    /// <summary>§11.12's "reputation hit, brief delist" on a driver-side cancel.</summary>
    public const string DriverCancelDelist = "driver_cancel_delist";

    /// <summary>Approaching a threshold; see <see cref="BlockStates.Warn"/>.</summary>
    public const string ApproachingThreshold = "approaching_threshold";

    /// <summary>An admin decision. Never recomputed away while it holds.</summary>
    public const string Manual = "manual";

    /// <summary>Nothing is wrong.</summary>
    public const string Clear = "clear";

    /// <summary>
    /// Whether a state with this reason survives a recompute until its time box runs out.
    /// </summary>
    /// <remarks>
    /// Only <see cref="DriverCancelDelist"/> does. It is imposed by an <em>event</em> — §11.12
    /// gives a driver the delist on their first cancel, with no threshold to fall back below — so
    /// nothing in the counters would reproduce it and the next counted fact would lift it.
    /// <para>
    /// <see cref="CancellationsDisabled"/> and <see cref="ReportsDelist"/> are deliberately not
    /// sticky: they are <em>derived</em> from counters, and a derived state has to follow its
    /// counters or D5' §7.2's "counter resets to 0 on any completed ride" could not re-enable a
    /// booking-disabled passenger — the cooldown would outlive the reason for it.
    /// </para>
    /// </remarks>
    public static bool SurvivesRecompute(string? reason) => reason is DriverCancelDelist;
}

/// <summary>Who last wrote <c>reputation.block_states</c>.</summary>
public static class BlockSources
{
    public const string Auto = "auto";
    public const string Manual = "manual";
}

/// <summary>The kinds of fact <c>reputation.intake_log</c> records.</summary>
public static class IntakeKinds
{
    public const string Cancellation = "cancellation";
    public const string NoShow = "no_show";
    public const string Report = "report";

    /// <summary>A completed ride — the only thing that resets the AL-16 consecutive run.</summary>
    public const string Completion = "completion";
}

/// <summary>Where a counted fact came from. Matches the <c>ck_intake_log_source</c> CHECK.</summary>
public static class IntakeSources
{
    public const string Grpc = "grpc";
    public const string RideEvents = "ride.events";
    public const string Admin = "admin";
}

/// <summary>
/// The E-07 detector's signal names, written to <c>reputation.fraud_flags.kind</c>.
/// </summary>
/// <remarks>
/// Open by design — 0802's header says a new detector must not need a migration. These three are
/// the ones ADD §12.6 and the replica layout name.
/// </remarks>
public static class FraudFlagKinds
{
    /// <summary>Same <c>(passenger, driver)</c> pair beyond N completed rides in 30 days.</summary>
    public const string RepeatPair = "repeat_pair";

    /// <summary>Two or more accounts sharing one device binding (<c>iam.devices.device_key</c>).</summary>
    public const string SharedDevice = "shared_device";

    /// <summary>Several accounts clustered on one address or autonomous system.</summary>
    public const string NetworkCluster = "network_cluster";
}

/// <summary>Review state of a flag (<c>reputation.yaml</c> <c>FraudFlag.status</c>).</summary>
public static class FraudFlagStatuses
{
    public const string Open = "open";
    public const string Dismissed = "dismissed";
    public const string Actioned = "actioned";

    public static bool IsResolution(string? status) => status is Dismissed or Actioned;
}
