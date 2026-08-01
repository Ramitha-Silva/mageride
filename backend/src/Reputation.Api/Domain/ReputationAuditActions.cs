namespace MageRide.Reputation.Domain;

/// <summary>
/// The <c>audit.events</c> actions and entity types this service writes (D-35).
/// </summary>
/// <remarks>
/// <b>The vocabulary is this service's; the writer is not.</b> The INSERT moved into the kernel as
/// <c>MageRide.Shared.Messaging.IAuditEventWriter</c> when C062 became its third caller — which is
/// what the C057 handoff asked for. What stays here is the set of facts reputation-svc is entitled
/// to record, because those are decisions of D5' §4.2 and E-07 and belong beside the rules that
/// reach them.
/// </remarks>
public static class ReputationAuditActions
{
    /// <summary>The block state was set by hand.</summary>
    public const string BlockStateOverride = "REPUTATION_BLOCK_STATE_OVERRIDE";

    /// <summary>A driver level was restored on appeal (US-6A.8).</summary>
    public const string LevelRestore = "REPUTATION_LEVEL_RESTORE";

    /// <summary>An E-07 flag was dismissed or actioned.</summary>
    public const string FlagResolved = "REPUTATION_FLAG_RESOLVED";

    /// <summary>A level was taken automatically. No actor — the rule decided (D5' §4.2).</summary>
    public const string LevelDecrement = "REPUTATION_LEVEL_DECREMENT";

    /// <summary><c>entity_type</c> for a block-state fact.</summary>
    public const string BlockStateEntity = "reputation.block_state";

    /// <summary><c>entity_type</c> for a level fact. The row is <c>dispatch.driver_levels</c>.</summary>
    public const string DriverLevelEntity = "dispatch.driver_level";

    /// <summary><c>entity_type</c> for an E-07 fraud flag.</summary>
    public const string FraudFlagEntity = "reputation.fraud_flag";
}
