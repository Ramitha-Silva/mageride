using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Eligibility;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Dispatching;

/// <summary>
/// D5' §3.2's hard gates and §3.3's versioned weighted score, applied to one round's candidates.
/// </summary>
public interface ICandidateScorer
{
    /// <summary>
    /// Gates and scores every candidate, eligible ones first and in the order the cascade will try
    /// them.
    /// </summary>
    IReadOnlyList<ScoredCandidate> Score(
        RideDispatchRequest ride,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<Guid, ReputationVerdict> reputation,
        IReadOnlyDictionary<Guid, WalletVerdict> wallet);
}

/// <inheritdoc cref="ICandidateScorer"/>
/// <remarks>
/// <para>
/// <b>The gates run before the score, and both are recorded.</b> D5' §3.2 is titled "Hard
/// eligibility gates (run BEFORE scoring)" and DT-05 repeats the ordering; the score is computed
/// for excluded candidates anyway, because R-11's audit is asked "why did this driver not get the
/// ride" far more often than "why did that one", and a row that says only <c>rejectedBy</c>
/// cannot distinguish a driver who was blocked from one who was blocked <em>and</em> was never
/// going to win.
/// </para>
/// <para>
/// <b>Three of §3.2's gates are not here, and that is on purpose.</b> The vehicle category, the
/// GPS freshness rule, <c>safety.blocked_drivers</c> and the E-03 <c>DISPATCH_SUSPENDED</c> state
/// are all predicates on rows Postgres already has in hand, so
/// <see cref="Persistence.CandidateRepository"/> applies them inside the same <c>ST_DWithin</c>
/// query rather than fetching a driver in order to reject them in C#. The two that need another
/// service — reputation-svc's block state and the wallet balance — arrive here as the two
/// dictionaries.
/// </para>
/// <para>
/// <b>Pure and synchronous.</b> Everything it needs has been fetched by the caller, which is what
/// lets the whole scoring rule be tested without a database, a broker or a clock.
/// </para>
/// </remarks>
public sealed class CandidateScorer(IOptions<DispatchOptions> options) : ICandidateScorer
{
    /// <summary>The name written into every breakdown at <see cref="DispatchOptions.AlgorithmVersion"/> 1.</summary>
    public const string AlgorithmName = "weighted-v1";

    /// <summary>D5' §3.3's denominator in <c>driverLevel / 3</c> — the top of the 1..3 range.</summary>
    private const double MaxDriverLevel = 3d;

    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public IReadOnlyList<ScoredCandidate> Score(
        RideDispatchRequest ride,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<Guid, ReputationVerdict> reputation,
        IReadOnlyDictionary<Guid, WalletVerdict> wallet)
    {
        ArgumentNullException.ThrowIfNull(ride);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(reputation);
        ArgumentNullException.ThrowIfNull(wallet);

        var weights = new ScoreTerms(_options.Weights.Distance, _options.Weights.Level, _options.Weights.Category);

        var evaluated = candidates
            .Select(candidate => Evaluate(ride, candidate, reputation, wallet, weights))
            .ToList();

        // Eligible first, then by score. The secondary key is the exact distance and the tertiary
        // the driver id: two candidates can tie on a weighted score to the last bit — the level
        // term takes three values and the category term one — and a cascade whose order depended on
        // the order Redis happened to return a set in would not be reproducible from the audit.
        var ordered = evaluated
            .OrderByDescending(static c => c.Eligible)
            .ThenByDescending(static c => c.Score)
            .ThenBy(static c => c.DistanceM)
            .ThenBy(static c => c.DriverId)
            .ToList();

        // Rank is the cascade position, so only an eligible candidate has one. -1 says "never in
        // the running", which is a different fact from "ranked last".
        var rank = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var candidate = ordered[i];
            var position = candidate.Eligible ? rank++ : -1;

            ordered[i] = candidate with { Breakdown = candidate.Breakdown with { Rank = position } };
        }

        return ordered;
    }

    private ScoredCandidate Evaluate(
        RideDispatchRequest ride,
        Candidate candidate,
        IReadOnlyDictionary<Guid, ReputationVerdict> reputation,
        IReadOnlyDictionary<Guid, WalletVerdict> wallet,
        ScoreTerms weights)
    {
        var block = reputation.TryGetValue(candidate.DriverId, out var verdict) ? verdict : ReputationVerdict.Unknown;
        var money = wallet.TryGetValue(candidate.DriverId, out var purse) ? purse : WalletVerdict.NotChecked;
        var packageCompatible = PackageCompatibility.Evaluate(candidate.VehicleType, ride.PackageSize);

        // Gate order follows D5' §3.2's own sentence order. It only matters for which reason ends
        // up on the audit row when a candidate fails more than one — and a driver who is delisted
        // *and* broke should read as delisted, because that is the one they can do least about.
        var rejectedBy =
            !block.DispatchEligible ? EligibilityGates.BlockState
            : !money.Allowed ? EligibilityGates.Wallet
            : packageCompatible is false ? EligibilityGates.PackageSize
            : null;

        // 1 / (1 + d/halfLife): monotonically decreasing, in (0,1], and finite for a driver
        // standing on the pickup — which the literal `1/distanceToPickup` of D5' §3.3 is not.
        var distanceTerm = 1d / (1d + (candidate.DistanceM / _options.DistanceHalfLifeM));
        var levelTerm = block.Level / MaxDriverLevel;

        // 1 for every candidate today: the tier is both a hard gate (§3.2) and the Redis index key,
        // so a candidate on the wrong tier is never in this list to begin with. The term is kept
        // because it is in the formula and because it is what would separate an exact match from a
        // larger tier serving a smaller request, if a tier fallback is ever introduced.
        var categoryTerm = string.Equals(candidate.VehicleType, ride.VehicleType, StringComparison.Ordinal) ? 1d : 0d;

        var terms = new ScoreTerms(distanceTerm, levelTerm, categoryTerm);

        var score = (weights.Distance * distanceTerm)
                    + (weights.Level * levelTerm)
                    + (weights.Category * categoryTerm);

        var breakdown = new ScoreBreakdown(
            Algorithm: AlgorithmName,
            Rank: -1,

            // Not rounded. R-11's audit has to let a decision be recomputed from the row alone, and
            // a distance rounded here would no longer produce the distance term stored beside it.
            DistanceM: candidate.DistanceM,
            DistanceHalfLifeM: _options.DistanceHalfLifeM,
            DriverLevel: block.Level,

            // The reputation state, not a boolean: an audit that says "eligible: false" cannot tell
            // BOOKING_DISABLED from DELISTED, and UNKNOWN says reputation-svc was not asked at all.
            BlockState: block.Known ? block.State : ReputationVerdict.Unknown.State,
            WalletOk: money.Allowed,
            PackageSizeCompatible: packageCompatible,
            RejectedBy: rejectedBy,
            Terms: terms,
            Weights: weights,

            // DT-02's slot. C036 fills it; a fabricated value here would be indistinguishable from
            // a predicate that had actually run.
            Directional: null);

        return new ScoredCandidate(candidate, rejectedBy is null, rejectedBy, score, breakdown);
    }
}
