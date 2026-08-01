using System.ComponentModel.DataAnnotations;

namespace MageRide.Payout.Configuration;

/// <summary>
/// payout-svc's own settings (D7' §4.2's <c>Payout__*</c> block, AL-58).
/// </summary>
/// <remarks>
/// Everything cross-cutting is the kernel's (<c>ConnectionStrings:Postgres</c>, <c>Jwt:*</c>).
/// Every knob here either switches off something that fails silently, or names a number no spec
/// pins — and each says which.
/// </remarks>
public sealed class PayoutOptions
{
    public const string SectionName = "Payout";

    /// <summary>
    /// Whether the weekly run executes at all.
    /// </summary>
    /// <remarks>
    /// <b>Off ⇒ nothing is ever swept and every driver's balance grows without bound.</b> No error
    /// is raised anywhere — a wallet that is filling up looks exactly like a busy week — so it is
    /// announced at start-up. The routes stay mapped, because Finance still needs to read what is
    /// owed.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The Asia/Colombo weekday the sweep runs on. Default Sunday.
    /// </summary>
    /// <remarks>
    /// <b>No spec names a day.</b> Sunday because a week that closes on it puts a driver's earnings
    /// in their account at the start of the next one, and because it is the quietest day for the
    /// bank rail to reject something an operator then has to look at.
    /// </remarks>
    public DayOfWeek RunDay { get; init; } = DayOfWeek.Sunday;

    /// <summary>
    /// How often the runner wakes to ask whether today's sweep has happened.
    /// </summary>
    /// <remarks>
    /// <b>An interval, not a weekly alarm</b> — fleet-billing-svc's argument, and it matters more
    /// here. The sweep is idempotent on the Colombo business date (<c>run_date</c> is UNIQUE), so
    /// re-asking costs one indexed read and catches everything an alarm would miss: a deployment
    /// rolling at midnight, a replica whose clock moved, a run that failed halfway. A weekly alarm
    /// gets exactly one chance per week to be running, and its failure mode is a week nobody is
    /// paid.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "12:00:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// What is held back from each sweep, in minor units.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zero, by decision: a full sweep with no minimum and no holdback</b> — whatever the balance
    /// is on run day is paid in full.
    /// </para>
    /// <para>
    /// The knob exists because of one named interaction (D5' §8.1): the D-08 daily fee is charged
    /// from the <b>second</b> trip of each Colombo day, and cash and driver-QR fares never credit
    /// the wallet — so a driver whose passengers pay in cash is swept to zero and refused their
    /// second trip until they top up. Setting this to one daily fee is the remedy, and it is a
    /// setting rather than a redesign precisely so the decision stays reversible.
    /// </para>
    /// </remarks>
    [Range(0, long.MaxValue)]
    public long RetainMinor { get; init; }

    /// <summary>How many drivers one sweep will process. A bound, not a working limit.</summary>
    /// <remarks><b>No spec.</b> Well above any plausible driver count for a launch market.</remarks>
    [Range(1, 100_000)]
    public int BatchSize { get; init; } = 5_000;

    /// <summary>wallet-svc's base URL — the ledger seam every movement goes through (D-09).</summary>
    /// <remarks>
    /// <b>Unset ⇒ no sweep can debit anything, so no instruction is raised and nothing is paid.</b>
    /// The run still selects and still reports what it would have moved. ERROR at start-up.
    /// </remarks>
    public string? WalletBaseUrl { get; init; }

    /// <summary>Must equal <c>Wallet:InternalApiKey</c>.</summary>
    public string? WalletInternalApiKey { get; init; }

    /// <summary>
    /// The bank origination endpoint (LankaPay/CEFTS via a sponsor bank, or an aggregator).
    /// </summary>
    /// <remarks>
    /// <b>Unset ⇒ instructions are raised and stay <c>PENDING</c>.</b> That is deliberate and is the
    /// design: the debit still happens, the row still records what is owed, and an operator can see
    /// the liability before a rail exists. <b>No provider is chosen</b> — ADD §1.18 makes the
    /// sponsor-bank relationship a go-live gate — so this is one outbound port and nothing more.
    /// ERROR at start-up.
    /// </remarks>
    public string? BankBaseUrl { get; init; }

    /// <summary>Credential for <see cref="BankBaseUrl"/>.</summary>
    public string? BankApiKey { get; init; }

    /// <summary>Budget for one outbound call.</summary>
    /// <remarks><b>No spec.</b> Longer than D6' §8.3's internal hop — a bank is not a pod.</remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan BankTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The shared secret on this service's own <c>/v1/internal/**</c> plane (C008).
    /// </summary>
    /// <remarks>
    /// <b>Unset ⇒ <c>POST /v1/internal/payouts/{id}/result</c> is not mapped at all</b>, so a bank
    /// can never report an outcome and every instruction stays <c>SUBMITTED</c> for ever. A 404 is
    /// the right failure for a deployment that forgot it — the alternative is an unauthenticated
    /// route that can mark somebody's money paid.
    /// </remarks>
    public string? InternalApiKey { get; init; }
}
