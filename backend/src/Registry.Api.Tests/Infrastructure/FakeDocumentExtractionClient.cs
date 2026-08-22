using System.Collections.Concurrent;
using System.Globalization;
using MageRide.Registry.Domain;
using MageRide.Registry.Onboarding;

namespace MageRide.Registry.Tests.Infrastructure;

/// <summary>
/// Stands in for ocr-svc (C054) at the <see cref="IDocumentExtractionClient"/> seam.
/// </summary>
/// <remarks>
/// <para>
/// Every AL-29/AL-30 rule under test is a decision registry-svc makes about <em>what ocr-svc
/// returned</em> — a confident field auto-verifies, a doubtful one does not, a plate that read as
/// something else is pending however sure the model was. Reproducing those inputs against the real
/// Gemini would mean carrying deliberately blurry photographs in the repository and hoping the
/// model stays as unsure of them as it is today. The port is the whole point: this class produces
/// the inputs, and the assertions are about registry-svc's half.
/// </para>
/// <para>
/// The default is a clean read of every document, because that is the path AL-27 auto-approves on
/// and the one most tests need as a starting point. <see cref="Responder"/> replaces it.
/// </para>
/// </remarks>
internal sealed class FakeDocumentExtractionClient : IDocumentExtractionClient
{
    /// <summary>Comfortably above <c>Registry:OcrConfidenceThreshold</c>'s 0.80 default.</summary>
    public const decimal Confident = 0.96m;

    /// <summary>Comfortably below it — the "doubtful" half of BR-25.2.</summary>
    public const decimal Doubtful = 0.42m;

    /// <summary>Every request registry-svc made, so a test can assert what was and was not sent.</summary>
    public ConcurrentQueue<DocumentExtractionRequest> Requests { get; } = new();

    /// <summary>Replaces the clean-read default. Null restores it.</summary>
    public Func<DocumentExtractionRequest, DocumentExtraction>? Responder { get; set; }

    /// <summary>Makes every call behave as if Gemini and Tesseract are both down (C054's fence).</summary>
    public void FailEverything() => Responder = _ => DocumentExtraction.Unavailable;

    /// <summary>Reads the plate as <paramref name="plate"/> whatever the vehicle is registered as.</summary>
    public void MisreadPlateAs(string plate) =>
        Responder = request => request.Kind == DocumentKinds.Registration
            ? new DocumentExtraction(true,
            [
                new ExtractedField(DocumentFieldKeys.PlateText, plate, Confident),
                // Confident, and wrong. The mismatch is the verdict, not the confidence — which is
                // the distinction D5' §14.1a's photos row makes and this test double preserves.
                new ExtractedField(
                    DocumentFieldKeys.RegNoMatch,
                    string.Equals(plate, request.RegistrationNumber, StringComparison.OrdinalIgnoreCase)
                        ? "true"
                        : "false",
                    Confident),
            ])
            : CleanRead(request);

    /// <summary>
    /// Reads the licence back the way the real model does: the CLASSES printed on the card
    /// (Δ MCS-11).
    /// </summary>
    /// <remarks>
    /// <see cref="CleanRead"/> answers <c>"three_wheeler,sedan"</c> — AL-09 vehicle types, which is
    /// what registry-svc wants but NOT what a driving licence says or what
    /// <c>GeminiPrompts</c> asks for ("the licence classes … exactly as printed"). That double kept
    /// this suite green through the entire defect: the moment extraction started working for real
    /// (MCS-07) the platform began answering <c>allowedVehicleTypes: ["B","G1"]</c> in a field the
    /// contract declares as a <c>VehicleType</c> enum, and every strict client failed the response.
    /// </remarks>
    public void ReadsLicenceClasses(string classes = "B,G1") =>
        Responder = request => request is { Kind: DocumentKinds.DrivingLicense, Side: DocumentSides.Back }
            ? new DocumentExtraction(true,
            [
                // Confident, and unusable. Same distinction as MisreadPlateAs above: the vocabulary
                // is the verdict, not the confidence.
                new ExtractedField(DocumentFieldKeys.AllowedVehicleTypes, classes, Confident),
            ])
            : CleanRead(request);

    /// <summary>Reads <paramref name="kind"/> correctly but without confidence (BR-25.2's "doubtful").</summary>
    public void ReadDoubtfully(string kind) =>
        Responder = request => request.Kind != kind
            ? CleanRead(request)
            : new DocumentExtraction(
                true,
                [.. CleanRead(request).Fields.Select(field => field with { Confidence = Doubtful })]);

    public Task<DocumentExtraction> ExtractAsync(
        DocumentExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Enqueue(request);

        return Task.FromResult((Responder ?? CleanRead)(request));
    }

    /// <summary>
    /// What ocr-svc returns for a well-captured document: every field D5' §14.1a's verdict table
    /// names, extracted with confidence, expiring a year or more from now.
    /// </summary>
    private static DocumentExtraction CleanRead(DocumentExtractionRequest request)
    {
        var expiry = Date(DateTimeOffset.UtcNow.AddYears(1));

        return (request.Kind, request.Side) switch
        {
            // Δ MCS-20 — the EXPIRY IS ON THE BACK, with the classes.
            //
            // `4a` on the front is the date of ISSUE; the expiry is column 11 of the class table on
            // the reverse. This double answered it on the front, which is where registry-svc used to
            // ask for it — so it agreed with the defect and three tests passed on the wrong layout.
            (DocumentKinds.DrivingLicense, DocumentSides.Back) => new DocumentExtraction(true,
            [
                new ExtractedField(DocumentFieldKeys.AllowedVehicleTypes, "three_wheeler,sedan", Confident),
                new ExtractedField(DocumentFieldKeys.LicenceExpiry, Date(DateTimeOffset.UtcNow.AddYears(5)), Confident),
            ]),
            (DocumentKinds.DrivingLicense, _) => new DocumentExtraction(true,
            [
                new ExtractedField(DocumentFieldKeys.LicenceNo, "B1234567", Confident),
                new ExtractedField(DocumentFieldKeys.NicNo, "199012345678", Confident),
            ]),
            (DocumentKinds.Insurance, _) => new DocumentExtraction(true,
            [
                new ExtractedField(DocumentFieldKeys.Insurer, "Ceylinco", Confident),
                new ExtractedField(DocumentFieldKeys.InsuranceExpiry, expiry, Confident),
            ]),
            (DocumentKinds.RevenueLicense, _) => new DocumentExtraction(true,
            [
                new ExtractedField(DocumentFieldKeys.RevenueNo, "RL-8891234", Confident),
                new ExtractedField(DocumentFieldKeys.RevenueExpiry, expiry, Confident),
            ]),
            (DocumentKinds.Registration, _) => new DocumentExtraction(true,
            [
                new ExtractedField(DocumentFieldKeys.PlateText, request.RegistrationNumber, Confident),
                new ExtractedField(DocumentFieldKeys.RegNoMatch, "true", Confident),
            ]),
            _ => DocumentExtraction.Unavailable,
        };
    }

    private static string Date(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
