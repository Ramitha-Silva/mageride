using System.Globalization;

namespace MageRide.Subscriptions.Domain;

/// <summary>`subscription.access_requests.status` (migration 1201).</summary>
public static class AccessRequestStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>`subscription.grants.status` (migration 1201).</summary>
/// <remarks>
/// There is no <c>deleted</c> value and there must not be: AL-25's "muted until the owner deletes
/// it" is <c>deleted_at</c>, a separate column, because the roster has to keep showing an
/// unsubscribed row and stop showing a deleted one. Folding the two into one enum would make
/// <c>ux_grant_active</c> — partial on <c>deleted_at IS NULL</c> — unexpressible.
/// </remarks>
public static class GrantStatuses
{
    public const string Active = "active";
    public const string Unsubscribed = "unsubscribed";
}

/// <summary>`subscription.subscriptions.status` (migration 1202).</summary>
public static class SubscriptionStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// `subscription.subscriptions.billing` — the AL-51 "Service payment" setting, whose values are
/// deliberately unchanged (label only).
/// </summary>
public static class SubscriptionBilling
{
    public const string Paid = "paid";
    public const string Free = "free";
}

/// <summary>`subscription.payments.method` (migration 1202, item 16).</summary>
public static class SubscriptionPayMethods
{
    public const string LankaQrDeeplink = "lankaqr_deeplink";
    public const string LankaQrScan = "lankaqr_scan";
    public const string Onepay = "onepay";
    public const string OnlineTransfer = "online_transfer";

    /// <summary>Handed to a collector; only the owner may say it arrived (US-23.6).</summary>
    public const string Cash = "cash";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        LankaQrDeeplink, LankaQrScan, Onepay, OnlineTransfer, Cash,
    };

    /// <summary>The two methods whose pay sheet renders the owner's bank-app QR image (AL-49).</summary>
    public static bool IsLankaQr(string method) =>
        string.Equals(method, LankaQrDeeplink, StringComparison.Ordinal)
        || string.Equals(method, LankaQrScan, StringComparison.Ordinal);
}

/// <summary>`subscription.payments.status` (migration 1202).</summary>
public static class SubscriptionPaymentStatuses
{
    public const string Initiated = "initiated";

    /// <summary>An online transfer whose slip is uploaded and awaiting the owner's confirm (US-23.4).</summary>
    public const string PendingVerification = "pending_verification";

    public const string Paid = "paid";
    public const string Failed = "failed";
}

/// <summary>
/// The billing cycle and its due-date arithmetic (BR-23.9, US-23.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Joined 5 June ⇒ next due 6 July.</b> BR-23.9's prose ("<c>next_due = join_date + 1 month</c>")
/// and its worked example disagree by a day; D4' §18b, D5' §BR-23.9 and this component's definition
/// of done all print the <b>example</b>, so the example is what is implemented. It is also the only
/// reading that makes sense of the money: the fare paid on the 5th of June buys the month up to and
/// including the 5th of July, so the next one falls due the day after it runs out. A due date on the
/// 5th would charge for a day already paid for.
/// </para>
/// <para>
/// <b>The anniversary is re-derived from <c>join_day</c> every time, never from the previous due
/// date.</b> A subscriber who joined on the 31st has no anniversary in February;
/// <c>DateOnly.AddMonths</c> clamps it to the 28th, and advancing from *that* would move them to the
/// 28th for ever. Re-deriving from the stored day restores the 31st in March — which is exactly why
/// <c>subscription.subscriptions.join_day</c> is a column rather than something computed from
/// <c>created_at</c>.
/// </para>
/// </remarks>
public static class SubscriptionCycles
{
    /// <summary>Billed from the 1st of each month, whenever the subscriber joined.</summary>
    public const string MonthFirst = "month_first";

    /// <summary>Billed from the anniversary of the join day — the schema default.</summary>
    public const string JoinAnniversary = "join_anniversary";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MonthFirst, JoinAnniversary,
    };

    /// <summary>
    /// The first payment due date for somebody joining on <paramref name="joinDate"/> (Asia/Colombo).
    /// </summary>
    public static DateOnly FirstDue(DateOnly joinDate, string cycle) =>
        string.Equals(cycle, MonthFirst, StringComparison.Ordinal)
            ? FirstOfNextMonth(joinDate)
            : joinDate.AddMonths(1).AddDays(1);

    /// <summary>
    /// The due date after <paramref name="currentDue"/> has been paid — "subsequent due dates roll
    /// monthly" (BR-23.9).
    /// </summary>
    /// <param name="joinDay">
    /// The day of the month the subscription anniversaries on. Ignored for
    /// <see cref="MonthFirst"/>; for <see cref="JoinAnniversary"/> a missing one falls back to the
    /// day <paramref name="currentDue"/> implies, which <c>ck_subscriptions_join_day</c> makes
    /// unreachable through this service but not through a hand-written row.
    /// </param>
    public static DateOnly Advance(DateOnly currentDue, string cycle, int? joinDay)
    {
        if (string.Equals(cycle, MonthFirst, StringComparison.Ordinal))
        {
            return FirstOfNextMonth(currentDue);
        }

        // The last day the month just paid for covered. The next one ends on the following
        // anniversary, and the next payment falls due the day after that.
        var periodEnd = currentDue.AddDays(-1);
        var anchor = joinDay ?? periodEnd.Day;
        var next = periodEnd.AddMonths(1);

        return OnDay(next.Year, next.Month, anchor).AddDays(1);
    }

    /// <summary>
    /// The <c>subscription.payments.period_month</c> a due date belongs to — always the first of
    /// the month (<c>ck_subscription_payments_period_first_day</c>).
    /// </summary>
    public static DateOnly PeriodOf(DateOnly due) => new(due.Year, due.Month, 1);

    /// <summary>Formats a business date the way every error message and log line on this surface does.</summary>
    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly FirstOfNextMonth(DateOnly from) => new DateOnly(from.Year, from.Month, 1).AddMonths(1);

    /// <summary>The given day of a month, clamped to that month's length (31 Jan → 28/29 Feb).</summary>
    private static DateOnly OnDay(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
}
