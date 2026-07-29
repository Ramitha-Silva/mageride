using System.Globalization;
using MageRide.Reputation.Configuration;
using MageRide.Reputation.Counters;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Persistence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Reputation.Detection;

/// <summary>
/// The E-07 anti-collusion detector: pair frequency, device-binding cross-check, IP/ASN clustering.
/// </summary>
/// <remarks>
/// <para>
/// <b>A flag is a review item and never an action.</b> ADD §12.6 has reputation-svc "emit
/// <c>fraud.suspected</c> to admin queue" and reserves the auto-suspend for a Tier-2 decision; this
/// component's second fence says the same thing — the service publishes state and dispatch and
/// admin act on it. Nothing here writes <c>reputation.block_states</c>. An admin who agrees with a
/// flag actions it and then overrides the block state, and both halves are audited.
/// </para>
/// <para>
/// <b>Exactly once per detection window.</b> Each signal carries a <c>windowKey</c> and
/// <c>ux_fraud_flags_window</c> refuses a second row for the same
/// <c>(kind, subject, counterparty, window)</c>. That is a database property, not a scheduler one:
/// the detector runs on every replica on a timer, two passes can overlap, and a check-then-insert
/// would let both find nothing and both write. It also means the interval can be tuned freely —
/// running every minute produces the same flags as running every hour, only sooner.
/// </para>
/// <para>
/// <b>Why the thresholds are loose.</b> Every one of these patterns has an honest explanation: a
/// twice-weekly commute is a repeat pair, a couple sharing a handset is a shared device, and a
/// shared 4G NAT is a network cluster. The flag says how many and lets a human decide; the
/// detector's job is to find the pattern, not to be right about it.
/// </para>
/// </remarks>
public interface ICollusionDetector
{
    /// <summary>Runs all three detectors and raises whatever is new. Returns the flags written.</summary>
    Task<IReadOnlyList<FraudFlagRow>> RunAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICollusionDetector"/>
public sealed class CollusionDetector(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    IIntakeLogRepository intakeLog,
    IDetectionRepository detection,
    IFraudFlagRepository flags,
    IOutboxWriter outbox,
    TimeProvider clock,
    IOptions<ReputationOptions> options,
    ILogger<CollusionDetector> logger) : ICollusionDetector
{
    private readonly ReputationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyList<FraudFlagRow>> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var collusion = _options.Collusion;
        var window = WindowKey(now, collusion.DetectionWindow);

        var signals = new List<FraudSignal>();

        // Detection reads outside a transaction: they are analytical scans over up to 30 days of
        // rows, and holding a transaction open across all three would pin a snapshot for the whole
        // pass while the intake path is trying to write. Each signal is then raised in its own
        // short transaction below.
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            signals.AddRange(await DetectRepeatPairsAsync(connection, collusion, now, window, cancellationToken));
            signals.AddRange(await DetectSharedDevicesAsync(connection, collusion, window, cancellationToken));
            signals.AddRange(await DetectNetworkClustersAsync(connection, collusion, now, window, cancellationToken));
        }

        if (signals.Count == 0)
        {
            return [];
        }

        var raised = new List<FraudFlagRow>(signals.Count);

        foreach (var signal in signals)
        {
            var flag = await RaiseAsync(signal, now, cancellationToken);

            if (flag is not null)
            {
                raised.Add(flag);
            }
        }

        if (raised.Count > 0)
        {
            logger.LogInformation(
                "E-07 pass raised {Raised} of {Detected} signals in window {Window}",
                raised.Count, signals.Count, window);
        }

        return raised;
    }

    // -------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<FraudSignal>> DetectRepeatPairsAsync(
        Npgsql.NpgsqlConnection connection, CollusionOptions collusion, DateTimeOffset now, string window,
        CancellationToken cancellationToken)
    {
        var pairs = await intakeLog.CountPairsAsync(
            connection, now - collusion.PairWindow, collusion.PairRideThreshold, collusion.MaxSignalsPerPass,
            cancellationToken);

        // The passenger is the subject and the driver the counterparty, consistently — E-07 writes
        // the pair as "(passenger, driver)" and a detector that sometimes flipped them would raise
        // the same pair twice under two keys and defeat ux_fraud_flags_window.
        return
        [
            .. pairs.Select(pair => new FraudSignal(
                Kind: FraudFlagKinds.RepeatPair,
                SubjectId: pair.PassengerId,
                SubjectType: "passenger",
                RelatedId: pair.DriverId,
                WindowKey: window,
                Summary: string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pair.Rides} completed rides with the same driver in {collusion.PairWindow.TotalDays:0} days (threshold {collusion.PairRideThreshold})."),
                Detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["rides"] = pair.Rides,
                    ["threshold"] = collusion.PairRideThreshold,
                    ["windowDays"] = collusion.PairWindow.TotalDays,
                    ["firstAt"] = pair.FirstAt,
                    ["lastAt"] = pair.LastAt,
                    ["passengerId"] = pair.PassengerId,
                    ["driverId"] = pair.DriverId,
                })),
        ];
    }

    private async Task<IReadOnlyList<FraudSignal>> DetectSharedDevicesAsync(
        Npgsql.NpgsqlConnection connection, CollusionOptions collusion, string window,
        CancellationToken cancellationToken)
    {
        var clusters = await detection.FindSharedDevicesAsync(
            connection, collusion.DeviceSharingThreshold, collusion.MaxSignalsPerPass, cancellationToken);

        var signals = new List<FraudSignal>();

        foreach (var cluster in clusters)
        {
            // One flag per account in the cluster, each naming the lowest other account as its
            // counterparty. An account has to be able to be reviewed on its own — the admin queue
            // is per subject, and a single flag naming five users would be actionable against none
            // of them. The ordering makes the counterparty deterministic, so re-running the
            // detector inside the window produces the same keys and inserts nothing.
            var ordered = cluster.UserIds.Order().ToArray();

            foreach (var userId in ordered)
            {
                var counterparty = ordered.First(other => other != userId);

                signals.Add(new FraudSignal(
                    Kind: FraudFlagKinds.SharedDevice,
                    SubjectId: userId,
                    SubjectType: "driver",
                    RelatedId: counterparty,
                    WindowKey: window,
                    Summary: string.Create(
                        CultureInfo.InvariantCulture,
                        $"{ordered.Length} accounts share one device binding."),
                    Detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["accounts"] = ordered.Length,
                        ["threshold"] = collusion.DeviceSharingThreshold,

                        // The device key itself is not published. It is a stable per-install
                        // identifier (AL-08) and the flag does not need it to be actionable — the
                        // account list is what an admin works from, and iam.devices is where the
                        // key stays.
                        ["userIds"] = ordered,
                    }));
            }
        }

        return signals;
    }

    private async Task<IReadOnlyList<FraudSignal>> DetectNetworkClustersAsync(
        Npgsql.NpgsqlConnection connection, CollusionOptions collusion, DateTimeOffset now, string window,
        CancellationToken cancellationToken)
    {
        var clusters = await detection.FindNetworkClustersAsync(
            connection, now - collusion.NetworkWindow, collusion.NetworkClusterThreshold,
            collusion.MaxSignalsPerPass, cancellationToken);

        var signals = new List<FraudSignal>();

        foreach (var cluster in clusters)
        {
            var ordered = cluster.UserIds.Order().ToArray();

            foreach (var userId in ordered)
            {
                var counterparty = ordered.First(other => other != userId);

                signals.Add(new FraudSignal(
                    Kind: FraudFlagKinds.NetworkCluster,
                    SubjectId: userId,
                    SubjectType: "passenger",
                    RelatedId: counterparty,
                    WindowKey: window,
                    Summary: string.Create(
                        CultureInfo.InvariantCulture,
                        $"{ordered.Length} accounts seen on one {cluster.Scope} in {collusion.NetworkWindow.TotalDays:0} days (threshold {collusion.NetworkClusterThreshold})."),
                    Detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["scope"] = cluster.Scope,

                        // The address is personal data (E-06) and the ASN is not, so only the
                        // non-personal scope key is published; an admin who needs the address reads
                        // reputation.network_observations, which is audited and retention-bound.
                        ["key"] = cluster.Scope == "ip" ? null : cluster.Key,
                        ["accounts"] = ordered.Length,
                        ["observations"] = cluster.Observations,
                        ["threshold"] = collusion.NetworkClusterThreshold,
                        ["windowDays"] = collusion.NetworkWindow.TotalDays,
                        ["userIds"] = ordered,
                    }));
            }
        }

        return signals;
    }

    /// <summary>
    /// Writes the flag and its <c>fraud.suspected</c> event in one transaction, or nothing.
    /// </summary>
    /// <remarks>
    /// The event is written only when the insert actually produced a row, so a repeated detection
    /// inside one window publishes nothing — which is the DoD's "exactly once per detection window"
    /// as the admin queue experiences it, not merely as the table does.
    /// </remarks>
    private async Task<FraudFlagRow?> RaiseAsync(
        FraudSignal signal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var flag = await flags.TryRaiseAsync(
            unitOfWork.Connection, unitOfWork.Transaction, signal, now, cancellationToken);

        if (flag is null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return null;
        }

        await outbox.WriteAsync(
            unitOfWork,
            ReputationEvents.FraudSuspected(flag, signal, Guid.NewGuid(), now),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return flag;
    }

    /// <summary>
    /// The bucket a detection belongs to: the start of the window containing <paramref name="now"/>,
    /// rendered ISO-8601 so it sorts and is legible in the admin queue.
    /// </summary>
    /// <remarks>
    /// Computed from the Unix epoch rather than from process start, so every replica agrees about
    /// which bucket it is in without coordinating. A whole-day window (the default) renders as a
    /// date, which is what the key looks like in practice.
    /// </remarks>
    internal static string WindowKey(DateTimeOffset now, TimeSpan window)
    {
        var ticks = now.UtcTicks - (now.UtcTicks % window.Ticks);
        var start = new DateTimeOffset(ticks, TimeSpan.Zero);

        return window >= TimeSpan.FromDays(1) && window.Ticks % TimeSpan.TicksPerDay == 0
            ? start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
