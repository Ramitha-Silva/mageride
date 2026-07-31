using MageRide.Ocr.Domain;

namespace MageRide.Ocr.Gemini;

/// <summary>
/// The extraction prompt per document kind (D6' §7.5's field → verdict mapping).
/// </summary>
/// <remarks>
/// <para>
/// <b>The prompt says what is missing from the image and why.</b> Every document reaching Gemini has
/// been through the D-36 pass, so the NIC and the licence number are black rectangles and the
/// portrait is blurred. A model told nothing about that reads the mask as a printing artefact and
/// invents a plausible number for it — which is exactly the failure mode I-25.1 guards against when
/// it says the NIC "value is captured from the structured response": the response has to be allowed
/// to say <em>redacted</em>.
/// </para>
/// <para>
/// <b>Confidence is asked for per field, and the prompt is explicit about what it means.</b> D6'
/// §7.5 and AL-29 both hang the whole verdict on "below threshold", and a model asked for a number
/// with no rubric returns 0.95 for everything. The rubric here is legibility of that field's own
/// characters, not the model's belief in its own answer.
/// </para>
/// </remarks>
internal static class GeminiPrompts
{
    private const string Preamble =
        "You are reading a photograph of a Sri Lankan vehicle or driver document for a ride-hailing "
        + "platform's onboarding check.\n"
        + "\n"
        + "The image has been redacted before it reached you, for privacy reasons:\n"
        + "  * any human face has been blurred;\n"
        + "  * national identity card and driving licence numbers have been covered with solid black "
        + "rectangles.\n"
        + "These are deliberate. NEVER guess, reconstruct or infer a value that is behind a black "
        + "rectangle or a blur. If a requested field is covered, return it with a null value.\n"
        + "\n"
        + "Return ONLY JSON of the form "
        + "{\"fields\":[{\"key\":\"...\",\"value\":\"...\"|null,\"confidence\":0.0-1.0}]}.\n"
        + "Include exactly one entry for every key listed below and no others.\n"
        + "\n"
        + "confidence is how clearly THAT FIELD'S OWN CHARACTERS are legible in the image:\n"
        + "  1.0  every character is sharp and unambiguous\n"
        + "  0.8  legible, with one or two characters you had to resolve from context\n"
        + "  0.5  partly obscured, blurred, glared or cropped — a human should check it\n"
        + "  0.0  not present, not legible, or covered by a redaction rectangle (value must be null)\n"
        + "Do not report high confidence for a value you inferred rather than read.\n"
        + "\n"
        + "Dates must be returned as yyyy-MM-dd. Sri Lankan documents print dates day-first.\n";

    /// <summary>The prompt for one document, listing exactly the keys its kind and side can carry.</summary>
    public static string For(string kind, string? side)
    {
        var keys = DocumentFieldKeys.AcceptedFor(kind, side);

        return Preamble + "\n" + Describe(kind, side) + "\n\nKeys to return:\n"
            + string.Join("\n", keys.Select(key => $"  {key} — {Explain(key)}"));
    }

    private static string Describe(string kind, string? side) => (kind, side) switch
    {
        (DocumentKinds.DrivingLicense, DocumentSides.Back) =>
            "This is the REVERSE of a Sri Lankan driving licence. It carries the table of licence "
            + "classes (A1, A, B1, B, C1, C, CE, D1, D, DE, G1, G, J) the holder is entitled to drive.",
        (DocumentKinds.DrivingLicense, _) =>
            "This is the FRONT of a Sri Lankan driving licence.",
        (DocumentKinds.Insurance, _) =>
            "This is a Sri Lankan motor insurance certificate or cover note.",
        (DocumentKinds.RevenueLicense, _) =>
            "This is a Sri Lankan revenue licence (vehicle licence) disc or certificate.",
        (DocumentKinds.Registration, _) =>
            "This is a photograph of a vehicle, or of its certificate of registration. What matters "
            + "is the registration number on the number plate or on the CR page.",
        (DocumentKinds.Permit, _) =>
            "This is a Sri Lankan route permit for a passenger-transport vehicle.",
        _ => "This is a vehicle or driver document.",
    };

    private static string Explain(string key) => key switch
    {
        DocumentFieldKeys.LicenceNo => "the licence number printed on the card",
        DocumentFieldKeys.LicenceExpiry => "the date of expiry of the licence",
        DocumentFieldKeys.NicNo =>
            "the holder's national identity card number — almost always redacted, so expect null",
        DocumentFieldKeys.AllowedVehicleTypes =>
            "the licence classes the holder may drive, comma-separated, exactly as printed (e.g. \"A1,B,C1\")",
        DocumentFieldKeys.InsuranceExpiry => "the date the cover expires",
        DocumentFieldKeys.InsurancePolicyNo => "the policy or cover-note number",
        DocumentFieldKeys.Insurer => "the name of the insurance company",
        DocumentFieldKeys.RevenueNo => "the revenue licence number",
        DocumentFieldKeys.RevenueExpiry => "the date the revenue licence expires",
        DocumentFieldKeys.PlateText => "the registration number as printed on the plate or the CR page",
        DocumentFieldKeys.RegNoMatch =>
            "leave this null — it is computed by the platform, not by you",
        DocumentFieldKeys.PermitNo => "the permit number",
        DocumentFieldKeys.PermitRoute => "the route or route number the permit is issued for",
        DocumentFieldKeys.PermitExpiry => "the date the permit expires",
        _ => "as printed",
    };
}
