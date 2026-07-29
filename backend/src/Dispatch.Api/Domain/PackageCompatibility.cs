namespace MageRide.Dispatch.Domain;

/// <summary>
/// P-11's static <c>vehicle_type × package_size</c> table — the hard gate that keeps an L parcel
/// off a motorbike.
/// </summary>
/// <remarks>
/// <para>
/// <b>No spec prints the table.</b> ADD §11 and the P-11 remediation row both say
/// "<c>dispatch.candidate_scores</c> adds <c>package_size_compatible BOOLEAN</c> derived from a
/// static <c>vehicle_type × package_size</c> table" and give exactly one cell of it —
/// "<c>Motorbike × L = false</c>" — and D5' §11 repeats the same example. The rest is this file's,
/// derived from AL-09's own split of the eight tiers ("Truck, Mini Truck" are the package-delivery
/// types; the six others are passenger tiers that can still take a parcel) and from what physically
/// fits: a motorbike takes a courier box, a three-wheeler or a car takes a boot-sized carton, and
/// only a van or a truck takes something a person cannot lift alone. Recorded as a
/// micro-change-set in the C034 handoff — <b>this table belongs in D5' §11 or in
/// <c>server_db_schema.md</c> §20 as seed data</b>, because it is a commercial policy and it is
/// edited far more often than code.
/// </para>
/// <para>
/// <b>The gate filters, it does not decide.</b> P-11 is explicit that an incompatible candidate is
/// removed "before offer, which preserves driver autonomy (drivers still see incoming requests with
/// size + description and can reject)". So this only ever narrows a round; a driver who *is*
/// compatible still gets the size on the offer card and may decline, and that decline is
/// downweighted rather than penalised (P-11, reputation-svc's business).
/// </para>
/// </remarks>
public static class PackageCompatibility
{
    /// <summary>What each tier can carry. Absent tier ⇒ carries nothing (fail closed).</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Carries =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            // A courier box between the rider's feet or on a rear rack. The one cell every spec
            // that mentions P-11 prints is `Motorbike × L = false`; M is excluded for the same
            // reason, one size down.
            ["motorbike"] = Set(PackageSizes.Small),

            // Passenger tiers: a boot or a footwell. Nothing here takes an L.
            ["three_wheeler"] = Set(PackageSizes.Small, PackageSizes.Medium),
            ["flex"] = Set(PackageSizes.Small, PackageSizes.Medium),
            ["sedan"] = Set(PackageSizes.Small, PackageSizes.Medium),

            // Load space rather than a boot.
            ["mini_van"] = Set(PackageSizes.Small, PackageSizes.Medium, PackageSizes.Large),
            ["van"] = Set(PackageSizes.Small, PackageSizes.Medium, PackageSizes.Large),

            // AL-09's two package-delivery tiers. Everything, by definition.
            ["mini_truck"] = Set(PackageSizes.Small, PackageSizes.Medium, PackageSizes.Large),
            ["truck"] = Set(PackageSizes.Small, PackageSizes.Medium, PackageSizes.Large),
        };

    /// <summary>
    /// Whether <paramref name="vehicleType"/> may carry <paramref name="packageSize"/>, or
    /// <see langword="null"/> when the ride carries no package at all.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> rather than <see langword="true"/> for a passenger ride, because that
    /// is what <c>dispatch.candidate_scores.package_size_compatible</c> means: migration 0703's own
    /// comment reads "P-11; NULL for non-package rides". A stored <c>true</c> would say the table
    /// was consulted and agreed.
    /// </remarks>
    public static bool? Evaluate(string? vehicleType, string? packageSize)
    {
        if (string.IsNullOrWhiteSpace(packageSize))
        {
            return null;
        }

        // An unknown size is refused rather than waved through: the only producer is
        // `rides.rides.package_size`, whose CHECK is S|M|L, so anything else is a wire-format
        // change nobody here has been told about.
        return PackageSizes.All.Contains(packageSize)
               && vehicleType is not null
               && Carries.TryGetValue(vehicleType, out var sizes)
               && sizes.Contains(packageSize);
    }

    private static IReadOnlySet<string> Set(params string[] sizes) =>
        new HashSet<string>(sizes, StringComparer.Ordinal);
}
