namespace MageRide.Wallet.Events;

/// <summary>
/// The event names on <c>wallet.events</c>.
/// </summary>
/// <remarks>
/// <b>D6' §2.2 prints no envelope for any of them</b>, so these shapes are this component's — recorded
/// in the C046 handoff. The two movement events are named by ADD §6 and by D5' §9.2
/// ("<c>wallet.debited</c> event clears" the D-08 cache); ride-svc's ADD row lists
/// <c>wallet.debited</c> among what it consumes.
/// </remarks>
internal static class WalletEventTypes
{
    /// <summary>Money left a wallet. Clears/refreshes the D-08 dispatch balance cache.</summary>
    public const string Debited = "wallet.debited";

    /// <summary>Money entered a wallet. Same consumer, same reason.</summary>
    public const string Credited = "wallet.credited";

    /// <summary>
    /// A wallet crossed below <c>Wallet:LowBalanceThresholdMinor</c> (US-9.9).
    /// </summary>
    /// <remarks>
    /// A hand-off to notification-svc (C051), not a notification: the payload carries the numbers and a
    /// <c>notificationType</c>, and no rendered text — the trilingual template, the channel and the
    /// driver's preferences are that service's (D-26). The same split C036 and C044 make.
    /// </remarks>
    public const string LowBalance = "wallet.low_balance";
}

/// <summary>The payloads <see cref="WalletEventTypes"/> carries.</summary>
internal static class WalletEvents
{
    /// <summary>
    /// One wallet movement.
    /// </summary>
    /// <remarks>
    /// <paramref name="amountMinor"/> is <b>signed as it was posted</b> — negative for a debit — so a
    /// consumer never has to infer direction from the event name and the two can never disagree.
    /// <paramref name="balanceAfterMinor"/> is what makes this event enough on its own: dispatch-svc can
    /// refresh its cache from the payload without reading anything.
    /// </remarks>
    public static object Movement(
        Guid ownerId,
        Guid accountId,
        string kind,
        long amountMinor,
        long balanceAfterMinor,
        DateTimeOffset at) =>
        new
        {
            ownerId,
            accountId,
            kind,
            amountMinor,
            balanceAfterMinor,
            currency = "LKR",
            occurredAt = at,
        };

    /// <summary>US-9.9's crossing.</summary>
    public static object LowBalance(
        Guid ownerId, long balanceMinor, long thresholdMinor, DateTimeOffset at) =>
        new
        {
            ownerId,
            balanceMinor,
            thresholdMinor,
            currency = "LKR",
            // D5' §9.4's second clause: below zero the app shows "Top Up Required" rather than a
            // low-balance nudge, and only the client can draw a banner — so the distinction travels
            // with the event instead of being re-derived from the number by every consumer.
            severity = balanceMinor < 0 ? "top_up_required" : "low",
            notificationType = "LOW_BALANCE",
            occurredAt = at,
        };
}
