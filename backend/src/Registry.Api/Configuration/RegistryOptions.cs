namespace MageRide.Registry.Configuration;

/// <summary>registry-svc's own settings.</summary>
public sealed class RegistryOptions
{
    public const string SectionName = "Registry";

    /// <summary>
    /// Whether <c>POST /v1/dev/vehicles/{vehicleId}/approve</c> is mapped at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> — the default — means "Development only". Set it explicitly to
    /// <see langword="true"/> for the lightweight production replica, which runs synthetic data
    /// under the Production environment name and still needs an approved vehicle to book a ride
    /// against (<c>specs/lightweight-production-replica.md</c>).
    /// </para>
    /// <para>
    /// This is a seed path, not the real approval. AL-10 makes a valid insurance document
    /// mandatory before any vehicle reaches <c>APPROVED</c>, and AL-30 derives approval from the
    /// four onboarding steps — <b>this endpoint checks neither</b>, because C021 is fenced out of
    /// document upload and OCR entirely. C029 owns the gate. When the route is off it is not
    /// mapped rather than answering 403, so nothing about it is discoverable in a deployment that
    /// did not ask for it.
    /// </para>
    /// </remarks>
    public bool? DevApprovalEnabled { get; set; }
}
