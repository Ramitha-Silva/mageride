using System.Collections.Concurrent;
using Grpc.Core;
using MageRide.Dispatch.Configuration;
using MageRide.Reputation.Grpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// The generated client is `MageRide.Reputation.Grpc.Reputation.ReputationClient`, and inside
// `MageRide.Dispatch.*` the leading `Reputation` binds to the `MageRide.Reputation` *namespace*
// before it ever reaches the class. The alias says which one is meant, once.
using ReputationClient = MageRide.Reputation.Grpc.Reputation.ReputationClient;

namespace MageRide.Dispatch.Eligibility;

/// <summary>
/// What reputation-svc says about one candidate (D-04).
/// </summary>
/// <param name="DispatchEligible">
/// reputation-svc's own verdict, precomputed server-side so every caller applies the same rule —
/// false for <c>BOOKING_DISABLED</c> and <c>DELISTED</c>, the two states D5' §3.2 excludes on.
/// </param>
/// <param name="Level">1..3, everyone starts at 3 (D5' §4.2). A scoring input, never a gate.</param>
/// <param name="Known">
/// <see langword="false"/> when the answer is a fallback rather than reputation-svc's — the gate
/// was disabled, or the call failed. Kept so the audit row can say "not asked" instead of "OK".
/// </param>
public sealed record ReputationVerdict(string State, bool DispatchEligible, int Level, bool Known)
{
    /// <summary>
    /// What a candidate gets when reputation-svc could not be asked.
    /// </summary>
    /// <remarks>
    /// <b>Fail open, deliberately.</b> A reputation outage that excluded every driver would take
    /// the whole platform down for a signal that removes a handful of them; ADD §12.6 already
    /// reserves punitive action for a decision somebody made, and D5' §3.2 describes an exclusion
    /// list rather than an allow-list. The Level falls back to the D5' §4.2 starting level for the
    /// same reason — assuming 1 would silently downweight every driver on the platform. Both are
    /// recorded on the candidate's audit row via <see cref="Known"/>.
    /// </remarks>
    public static readonly ReputationVerdict Unknown = new("UNKNOWN", DispatchEligible: true, Level: 3, Known: false);
}

/// <summary>
/// The D5' §3.2 block-status gate and the §3.3 Driver Level scoring input, over
/// <c>reputation.v1.Reputation</c> (D3', C033).
/// </summary>
public interface IReputationGate
{
    /// <summary>
    /// Asks about every candidate of one round at once. Never throws: an unreachable reputation-svc
    /// degrades to <see cref="ReputationVerdict.Unknown"/> for the drivers it could not answer for.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ReputationVerdict>> EvaluateAsync(
        IReadOnlyCollection<Guid> driverIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReputationGate"/>
/// <remarks>
/// <para>
/// <b>Two round trips per candidate, and both are cached.</b> D3' splits the block state and the
/// level across <c>GetBlockStatus</c> and <c>GetDriverLevel</c>, and dispatch needs both on the same
/// hot path: the first is a gate, the second a scoring term. reputation-svc serves the first from a
/// 5 s Redis cache and answers inside its 20 ms p95 budget (C033 DoD); this holds a matching
/// in-process memo so a cascade that re-evaluates the same neighbourhood eight times in two minutes
/// does not make sixteen calls per driver per round.
/// </para>
/// <para>
/// <b>The gate is applied to the answer, not computed from it.</b> <c>BlockStatus.dispatch_eligible</c>
/// is reputation-svc's own precomputed verdict — C033's handoff names it as the thing C034 should
/// read — so a later change to what "excluded" means lands in one service rather than in every
/// caller that re-derived it from the state enum.
/// </para>
/// </remarks>
public sealed class ReputationGate(
    ReputationClient client,
    IOptions<DispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<ReputationGate> logger) : IReputationGate
{
    /// <summary>
    /// The interim shared secret C033's gRPC service checks, until the C042 mesh replaces it with
    /// an mTLS peer identity. Lower-case: gRPC metadata keys are case-sensitive on the wire.
    /// </summary>
    public const string InternalKeyHeader = "x-mageride-internal-key";

    private readonly ConcurrentDictionary<Guid, CachedVerdict> _cache = new();
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyDictionary<Guid, ReputationVerdict>> EvaluateAsync(
        IReadOnlyCollection<Guid> driverIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driverIds);

        var verdicts = new Dictionary<Guid, ReputationVerdict>(driverIds.Count);

        if (!_options.ReputationGateEnabled)
        {
            // Not "everybody is OK" — "nobody was asked". The candidates carry Known: false into
            // the audit, so a round taken with the gate off is visibly different from one taken
            // with it on and clean.
            foreach (var driverId in driverIds)
            {
                verdicts[driverId] = ReputationVerdict.Unknown;
            }

            return verdicts;
        }

        var now = timeProvider.GetUtcNow();
        var misses = new List<Guid>();

        foreach (var driverId in driverIds)
        {
            if (_cache.TryGetValue(driverId, out var cached) && cached.ExpiresAt > now)
            {
                verdicts[driverId] = cached.Verdict;
            }
            else if (!verdicts.ContainsKey(driverId))
            {
                misses.Add(driverId);
            }
        }

        if (misses.Count == 0)
        {
            return verdicts;
        }

        // Concurrently, because a round's candidates are independent and the whole point of the
        // 20 ms p95 is that a ten-candidate round costs one round trip's latency, not ten.
        var fetched = await Task.WhenAll(misses.Select(driverId => FetchAsync(driverId, cancellationToken)));

        foreach (var (driverId, verdict) in fetched)
        {
            verdicts[driverId] = verdict;

            if (verdict.Known && _options.ReputationCacheTtl > TimeSpan.Zero)
            {
                _cache[driverId] = new CachedVerdict(verdict, now.Add(_options.ReputationCacheTtl));
            }
        }

        return verdicts;
    }

    private async Task<(Guid DriverId, ReputationVerdict Verdict)> FetchAsync(
        Guid driverId, CancellationToken cancellationToken)
    {
        var request = new DriverRef { UserId = driverId.ToString() };

        try
        {
            var deadline = DateTime.UtcNow.Add(_options.ReputationTimeout);

            var status = await client.GetBlockStatusAsync(
                request, Headers(), deadline, cancellationToken);

            var level = await client.GetDriverLevelAsync(
                request, Headers(), deadline, cancellationToken);

            return (driverId, new ReputationVerdict(
                NameOf(status.State), status.DispatchEligible, level.Level_, Known: true));
        }
        catch (RpcException exception)
        {
            // Information, not an error: D6' §8.3 makes the resilience policy the caller's, and
            // this is the policy — one attempt inside the round's own budget, then fall open with
            // the fact recorded. A retry here would spend the passenger's 15-second offer window.
            logger.LogWarning(
                "reputation-svc answered {StatusCode} for driver {DriverId}; the candidate is scored " +
                "as reputation-unknown (fail-open, recorded on the audit row)",
                exception.StatusCode, driverId);

            return (driverId, ReputationVerdict.Unknown);
        }
    }

    /// <summary>
    /// The state as <c>reputation.block_states.state</c>'s CHECK spells it (server_db_schema.md §7),
    /// not as protobuf's generated C# identifier.
    /// </summary>
    /// <remarks>
    /// <c>BlockState.Ok.ToString()</c> is <c>"Ok"</c> and <c>BookingDisabled</c> is
    /// <c>"BookingDisabled"</c> — protobuf's naming, not the platform's. This value is written into
    /// <c>candidate_scores.breakdown.blockState</c>, which an operator greps and joins against the
    /// reputation tables, so it has to be the spelling those tables use.
    /// </remarks>
    private static string NameOf(BlockState state) => state switch
    {
        BlockState.Ok => "OK",
        BlockState.Warn => "WARN",
        BlockState.BookingDisabled => "BOOKING_DISABLED",
        BlockState.Delisted => "DELISTED",
        _ => ReputationVerdict.Unknown.State,
    };

    private Metadata Headers()
    {
        var metadata = new Metadata();

        if (!string.IsNullOrWhiteSpace(_options.ReputationInternalKey))
        {
            metadata.Add(InternalKeyHeader, _options.ReputationInternalKey);
        }

        return metadata;
    }

    private sealed record CachedVerdict(ReputationVerdict Verdict, DateTimeOffset ExpiresAt);
}
