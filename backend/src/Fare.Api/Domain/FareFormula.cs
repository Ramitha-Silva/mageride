namespace MageRide.Fare.Domain;

/// <summary>
/// One <c>fares.tariffs</c> row — the Mode C rate in force for a vehicle type (migration 1001).
/// </summary>
/// <param name="PeakSurchargePct">
/// D5' §1.1's <c>tariff.peak_surcharge_pct</c>. The <b>tariff</b> decides how much a peak costs;
/// <see cref="PeakWindow"/> decides only when a peak is.
/// </param>
public sealed record Tariff(
    Guid Id,
    string VehicleType,
    long FirstKmMinor,
    long PerKmMinor,
    int PeakSurchargePct,
    int NightSurchargePct,
    string Currency,
    DateTimeOffset EffectiveFrom);

/// <summary>
/// One <c>fares.peak_windows</c> row: a recurring daily window in Asia/Colombo wall-clock (D-38).
/// </summary>
public sealed record PeakWindow(Guid Id, string Kind, TimeOnly StartLocal, TimeOnly EndLocal, int MultiplierPct)
{
    /// <summary>The two kinds the CHECK admits.</summary>
    public const string Peak = "peak";
    public const string Night = "night";

    /// <summary>
    /// Whether <paramref name="local"/> falls in this window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Half-open, <c>[start, end)</c>.</b> The seeded windows are 07:00–09:00, 17:00–19:00 and
    /// 22:00–05:00, which tile the day only if one endpoint is exclusive; treating both as
    /// inclusive would make 09:00 both peak and not, depending on which row was read first.
    /// </para>
    /// <para>
    /// <b>A window may wrap midnight, and the night one does.</b> Migration 1001 says so at the
    /// column and declines to add a CHECK for exactly this reason, so <c>end &lt; start</c> is not a
    /// bad row — it is 22:00–05:00, and a naive <c>start &lt;= t &amp;&amp; t &lt; end</c> would make the night
    /// surcharge unreachable rather than merely wrong.
    /// </para>
    /// </remarks>
    public bool Covers(TimeOnly local) =>
        StartLocal <= EndLocal
            ? local >= StartLocal && local < EndLocal
            : local >= StartLocal || local < EndLocal;
}

/// <summary>What the fare was made of — D3' <c>FareBreakdown</c>, for support and receipts.</summary>
/// <remarks>
/// <b>US-8.4 shows the total only.</b> The parts exist so a passenger disputing a fare, or Finance
/// reconciling one, can see how it was reached — not so the booking screen can display them.
/// </remarks>
public sealed record FareBreakdown(
    long FirstKmMinor,
    long PerKmMinor,
    double DistanceKm,
    int PeakSurchargePct,
    int NightSurchargePct,
    long BaseMinor,
    long SurchargeMinor,
    long TotalMinor,
    string Currency);

/// <summary>
/// D5' §1.1's master formula, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Money never touches a floating-point type</b> (D5' §1.3, and this component's definition of
/// done). The one measurement that genuinely is a real number — the distance — is converted to
/// whole metres at the boundary and never appears in an arithmetic expression with a money value
/// again. Everything downstream of <see cref="MetresOf"/> is <see cref="long"/>: no <c>double</c>,
/// no <c>decimal</c>, and therefore no representation error that could put a rupee between the
/// receipt and the ledger.
/// </para>
/// <para>
/// <b>One <c>round</c> per product, away from zero.</b> §1.3 says to compute in minor units and
/// round only where a <c>* pct / 100</c> product is taken; rounding at each additive step is
/// explicitly avoided. Away from zero rather than banker's, because every amount here is
/// non-negative and a passenger reading "Rs 480" should not have to know which way 0.5 fell. In
/// integer arithmetic that is <c>(a * b + half) / divisor</c>, which is exact.
/// </para>
/// <para>
/// <b>Peak and night stack additively on the base</b>, not multiplicatively on each other: §1.1
/// computes <c>round(baseMinor * (peakPct + nightPct) / 100)</c> as a single product, so a trip
/// that is both is base × 35%, never base × 1.20 × 1.15. The seeded windows never overlap, but an
/// admin may make them, and the formula answers the same way either way.
/// </para>
/// </remarks>
public static class FareFormula
{
    /// <summary>The first kilometre is inside the first-km charge (D5' §1.1).</summary>
    public const long IncludedMetres = 1_000;

    /// <summary>LKR is the only currency the platform transacts in.</summary>
    public const string Currency = "LKR";

    /// <summary>
    /// Prices a trip.
    /// </summary>
    /// <param name="distanceKm">
    /// Route distance for an estimate, Kalman-filtered track distance for a settlement (D5' §1.2).
    /// The formula does not care which — §1.4: "same engine".
    /// </param>
    /// <param name="isPeak">Whether <c>rideTime</c> fell in a peak window, evaluated in Asia/Colombo.</param>
    public static FareBreakdown Price(Tariff tariff, double distanceKm, bool isPeak, bool isNight)
    {
        ArgumentNullException.ThrowIfNull(tariff);

        var metres = MetresOf(distanceKm);

        // max(0, distanceKm - 1.0) — a trip shorter than the included kilometre costs the first-km
        // charge and nothing more, and never less than it.
        var extraMetres = Math.Max(0, metres - IncludedMetres);

        // round(extraKm * perKmMinor), done as whole metres against a per-kilometre rate so the
        // product is integral. This is the only place the distance meets a money value.
        var baseMinor = tariff.FirstKmMinor + DivideRounded(extraMetres * tariff.PerKmMinor, 1_000);

        var peakPct = isPeak ? tariff.PeakSurchargePct : 0;
        var nightPct = isNight ? tariff.NightSurchargePct : 0;

        var surchargeMinor = DivideRounded(baseMinor * (peakPct + nightPct), 100);

        return new FareBreakdown(
            FirstKmMinor: tariff.FirstKmMinor,
            PerKmMinor: tariff.PerKmMinor,
            DistanceKm: distanceKm,
            PeakSurchargePct: peakPct,
            NightSurchargePct: nightPct,
            BaseMinor: baseMinor,
            SurchargeMinor: surchargeMinor,
            TotalMinor: baseMinor + surchargeMinor,
            Currency: tariff.Currency);
    }

    /// <summary>
    /// The distance as whole metres — the boundary at which the measurement stops being a real
    /// number and the arithmetic becomes exact.
    /// </summary>
    /// <remarks>
    /// A negative distance is clamped rather than refused: it can only arise from a caller's bad
    /// input, and a fare of "the first-km charge" is a defensible answer where an exception on the
    /// completion path is not.
    /// </remarks>
    public static long MetresOf(double distanceKm) =>
        double.IsFinite(distanceKm) && distanceKm > 0
            ? (long)Math.Round(distanceKm * 1_000, MidpointRounding.AwayFromZero)
            : 0;

    /// <summary>
    /// <c>round(value / divisor)</c> in integer arithmetic, half away from zero.
    /// </summary>
    /// <remarks>
    /// Both arguments are non-negative on every path here — a fare, a rate and a percentage all
    /// are — but the negative branch is written rather than assumed, because "away from zero" and
    /// "half up" stop being the same rule the first time somebody prices a discount with it.
    /// </remarks>
    internal static long DivideRounded(long value, long divisor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(divisor);

        var half = divisor / 2;

        return value >= 0
            ? (value + half) / divisor
            : -((-value + half) / divisor);
    }
}
