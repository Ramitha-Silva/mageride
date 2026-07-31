using MageRide.Fleet.Documents;
using MageRide.Fleet.Domain;

namespace MageRide.Fleet.Tests.Infrastructure;

/// <summary>
/// An ocr-svc that answers whatever a test needs it to.
/// </summary>
/// <remarks>
/// <para>
/// AL-50's slot rule turns on what came back from extraction, so a suite that could not control
/// that could only ever assert the <c>missing</c> and <c>pending</c> halves of it — and the
/// approval gate, which is C059's first definition-of-done item, is about <c>verified</c>.
/// Registered through <c>FleetHarness.StartAsync(configure:)</c>, which runs before
/// <c>AddFleetServices</c>: the service registers both real implementations with
/// <c>TryAddSingleton</c>, so whatever is already there wins.
/// </para>
/// <para>
/// It returns exactly the shape ocr-svc's contract does — a key, a value and a confidence — and
/// nothing about verdicts, because deciding those is fleet-svc's half of C054's fence and is what
/// the tests are checking.
/// </para>
/// </remarks>
internal sealed class StubExtractionClient : IVehicleDocumentExtractionClient
{
    /// <summary>The plate a registration copy is read as. Defaults to matching, so the slot verifies.</summary>
    public string RegNoMatch { get; set; } = "true";

    /// <summary>What every field comes back with. Below <c>Fleet:OcrConfidenceThreshold</c> is pending.</summary>
    public decimal Confidence { get; set; } = 0.95m;

    /// <summary>The expiry every dated document is read as.</summary>
    public DateOnly Expiry { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1);

    /// <summary>When false, every call answers <c>Unavailable</c> — ocr-svc down or unconfigured.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Kinds this stub refuses to read, so one slot can be held pending while others verify.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    public int Calls { get; private set; }

    public Task<VehicleDocumentExtraction> ExtractAsync(
        VehicleDocumentExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Calls++;

        if (!Available || Unreadable.Contains(request.Kind))
        {
            return Task.FromResult(VehicleDocumentExtraction.Unavailable);
        }

        var expiry = Expiry.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        IReadOnlyList<ExtractedDocumentField> fields = request.Kind switch
        {
            VehicleDocumentKinds.Registration =>
            [
                new(VehicleDocumentFieldKeys.RegNoMatch, RegNoMatch, Confidence),
                new(VehicleDocumentFieldKeys.PlateText, request.RegistrationNumber, Confidence),
            ],
            VehicleDocumentKinds.Insurance =>
            [
                new(VehicleDocumentFieldKeys.InsuranceExpiry, expiry, Confidence),
            ],
            VehicleDocumentKinds.RevenueLicense =>
            [
                new(VehicleDocumentFieldKeys.RevenueNo, "RL-2026-004417", Confidence),
                new(VehicleDocumentFieldKeys.RevenueExpiry, expiry, Confidence),
            ],
            VehicleDocumentKinds.Permit =>
            [
                new(VehicleDocumentFieldKeys.PermitNo, "SLTB/138/2026", Confidence),
                new(VehicleDocumentFieldKeys.PermitRoute, "138 Colombo–Homagama", Confidence),
                new(VehicleDocumentFieldKeys.PermitExpiry, expiry, Confidence),
            ],
            _ => [],
        };

        return Task.FromResult(new VehicleDocumentExtraction(true, fields, Guid.CreateVersion7()));
    }
}
