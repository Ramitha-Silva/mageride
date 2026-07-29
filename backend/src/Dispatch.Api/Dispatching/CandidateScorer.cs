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
        IReadOnlyDictionary<Guid, WalletVerdict> wallet,
        IReadOnlyDictionary<Guid, DirectionalVerdict>? directional = null,
        CandidateOrdering ordering = CandidateOrdering.WeightedScore);
}

/// <summary>Which rule decides who the cascade tries first.</summary>
public enum CandidateOrdering
{
    /// <summary>D5' §3.3's weighted score — every ordinary Mode C round.</summary>
    WeightedScore,

    /// <summary>
    /// D5' §3.7's Job Board rule: "dispatched to closest intent-submitting driver by Level (ties →
    /// higher level rung first)". Distance decides; the level breaks the tie.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> a re-weighting of the §3.3 score. §3.7 is a different rule for a
    /// different situation — every candidate here chose this ride half an hour ago, so proximity is
    /// the only thing left that serves the passenger — and expressing it as weights would make the
    /// two indistinguishable in the audit and would put a distant Level-3 driver ahead of a nearby
    /// Level-2 one, which is exactly what "closest … by Level" does not say.
    /// </remarks>
    JobBoardProximity,
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
/// service — reputation-svc's block state and the wallet balance — arrive here as dictionaries, and
/// so does the DT-02 Directional Travel verdict, which needs a per-driver row and a per-round
/// geometry that no <c>ST_DWithin</c> predicate could express.
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
        IReadOnlyDictionary<Guid, WalletVerdict> wallet,
        IReadOnlyDictionary<Guid, DirectionalVerdict>? directional = null,
        CandidateOrdering ordering = CandidateOrdering.WeightedScore)
    {
        ArgumentNullException.ThrowIfNull(ride);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(reputation);
        ArgumentNullException.ThrowIfNull(wallet);

        var weights = new ScoreTerms(_options.Weights.Distance, _options.Weights.Level, _options.Weights.Category);

        var evaluated = candidates
            .Select(candidate => Evaluate(ride, candidate, reputation, wallet, directional, weights, ordering))
            .ToList();

        // Eligible first. After that the two rules diverge, and both end on the driver id: two
        // candidates can tie on a weighted score to the last bit — the level term takes three
        // values and the category term one — and a cascade whose order depended on the order Redis
        // happened to return a set in would not be reproducible from the audit.
        var ordered = (ordering is CandidateOrdering.JobBoardProximity
                ? evaluated
                    .OrderByDescending(static c => c.Eligible)
                    .ThenBy(static c => c.DistanceM)
                    .ThenByDescending(static c => c.Breakdown.DriverLevel)
                    .ThenBy(static c => c.DriverId)
                : evaluated
                    .OrderByDescending(static c => c.Eligible)
                    .ThenByDescending(static c => c.Score)
                    .ThenBy(static c => c.DistanceM)
                    .ThenBy(static c => c.DriverId))
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

    /// <summary>The <c>breakdown.ordering</c> value on a D5' §3.7 Job Board dispatch.</summary>
    public const string JobBoardOrderingName = "job-board-proximity";

    private ScoredCandidate Evaluate(
        RideDispatchRequest ride,
        Candidate candidate,
        IReadOnlyDictionary<Guid, ReputationVerdict> reputation,
        IReadOnlyDictionary<Guid, WalletVerdict> wallet,
        IReadOnlyDictionary<Guid, DirectionalVerdict>? directional,
        ScoreTerms weights,
        CandidateOrdering ordering)
    {
        var block = reputation.TryGetValue(candidate.DriverId, out var verdict) ? verdict : ReputationVerdict.Unknown;
        var money = wallet.TryGetValue(candidate.DriverId, out var purse) ? purse : WalletVerdict.NotChecked;
        var packageCompatible = PackageCompatibility.Evaluate(candidate.VehicleType, ride.PackageSize);

        var heading = directional?.TryGetValue(candidate.DriverId, out var filtered) is true
            ? filtered
            : DirectionalVerdict.NoFilter;

        // Gate order follows D5' §3.2's own sentence order. It only matters for which reason ends
        // up on the audit row when a candidate fails more than one — and a driver who is delisted
        // *and* broke should read as delisted, because that is the one they can do least about.
        //
        // **Directional is last, and DT-05 is why.** "The predicate runs after all hard gates and
        // never relaxes them — it can only remove otherwise-eligible candidates." Putting it at the
        // end of this chain is that sentence: a driver the wallet gate already refused reads as
        // refused by the wallet, and a driver nothing else refused is the only kind this clause can
        // reach. It cannot re-admit anybody, because it only ever writes a rejection.
        var rejectedBy =
            !block.DispatchEligible ? EligibilityGates.BlockState
            : !money.Allowed ? EligibilityGates.Wallet
            : packageCompatible is false ? EligibilityGates.PackageSize
            : !heading.Allowed ? EligibilityGates.Directional
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

            // DT-02's audit (Δ C036). Present only for a driver who had an active Destination
            // Filter, because "no member" and "the predicate ran and passed" are different facts
            // and the overwhelming majority of rows are the former.
            Directional: heading.Breakdown,

            // Omitted on the ordinary path, where Rank follows the score and saying so would be
            // noise on every row of every round.
            Ordering: ordering is CandidateOrdering.JobBoardProximity ? JobBoardOrderingName : null);

        return new ScoredCandidate(candidate, rejectedBy is null, rejectedBy, score, breakdown);
    }
}
