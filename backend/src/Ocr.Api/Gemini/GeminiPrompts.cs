using MageRide.Ocr.Domain;

namespace MageRide.Ocr.Gemini;

/// <summary>
/// The extraction prompt per document kind (D6' §7.5's field → verdict mapping).
/// </summary>
/// <remarks>
/// <para>
/// <b>There are two preambles, and sending the wrong one is worse than sending neither</b>
/// (Δ MCS-07). A document that went through the D-36 pass reaches the model with the NIC and the
/// licence number under black rectangles and the portrait blurred; told nothing about that, a model
/// reads the mask as a printing artefact and invents a plausible number for it — the failure mode
/// I-25.1 guards against when it says the NIC "value is captured from the structured response".
/// Told the <em>opposite</em> — that a document it can see perfectly well has been masked — it
/// returns null for fields that are plainly legible, which is the same bug pointing the other way.
/// So the preamble is chosen by what actually happened to the image, never by policy.
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
    private const string Opening =
        "You are reading a photograph of a Sri Lankan vehicle or driver document for a ride-hailing "
        + "platform's onboarding check.\n";

    /// <summary>What is added when the image went through the D-36 pre-pass.</summary>
    private const string RedactedNotice =
        "\n"
        + "The image has been redacted before it reached you, for privacy reasons:\n"
        + "  * any human face has been blurred;\n"
        + "  * national identity card and driving licence numbers have been covered with solid black "
        + "rectangles.\n"
        + "These are deliberate. NEVER guess, reconstruct or infer a value that is behind a black "
        + "rectangle or a blur. If a requested field is covered, return it with a null value.\n";

    /// <summary>What is added when it did not, and the document is as photographed (Δ MCS-07).</summary>
    /// <remarks>
    /// It still has to say "do not invent", because that instruction was doing two jobs in the
    /// redacted notice: protecting the masks, and protecting every field that is simply too blurred
    /// or too cropped to read. Only the first of those goes away with the pre-pass.
    /// </remarks>
    private const string UnredactedNotice =
        "\n"
        + "The image is the document as photographed, with nothing masked or removed. Read only "
        + "what is actually printed on it: NEVER guess, reconstruct or infer a value you cannot "
        + "see. If a requested field is absent, illegible, cropped or obscured, return it with a "
        + "null value.\n";

    /// <summary>
    /// What the model is asked to say the document IS, before it reads anything off it (Δ MCS-21).
    /// </summary>
    /// <remarks>
    /// The kind reaches this service as the CALLER's assertion, and it used to be stated to the
    /// model as fact — so the model was never in a position to disagree with it. Asking for the
    /// identification and putting it FIRST in the answer is deliberate: a model that has already
    /// written out an insurance expiry is answering "what is this?" against its own last sentence.
    ///
    /// <c>unclear</c> is offered explicitly and described generously, because the safety of the
    /// whole feature rests on the model being willing to say it rather than picking the nearest
    /// label. Nothing is ever done on the strength of <c>unclear</c>.
    /// </remarks>
    private const string Classification =
        "\n"
        + "document_type is what the image ACTUALLY shows, chosen from exactly these:\n"
        + "  driving_licence  a driving licence card, either side\n"
        + "  insurance        a motor insurance certificate or cover note\n"
        + "  revenue_licence  a vehicle revenue licence (road-tax disc or certificate)\n"
        + "  vehicle_photo    a photograph of a vehicle itself, or of its number plate\n"
        + "  other            a document, but none of the above\n"
        + "  unclear          too blurred, dark, cropped or partial to identify with confidence\n"
        + "Answer what you SEE, not what you are told below. If you are not sure, answer unclear — "
        + "that is a normal answer and it is preferred to a guess.\n";

    private const string Body =
        "\n"
        + "Return ONLY JSON of the form "
        + "{\"document_type\":\"...\","
        + "\"fields\":[{\"key\":\"...\",\"value\":\"...\"|null,\"confidence\":0.0-1.0}]}.\n"
        + "Include exactly one entry for every key listed below and no others.\n"
        + "\n"
        + "confidence is how clearly THAT FIELD'S OWN CHARACTERS are legible in the image:\n"
        + "  1.0  every character is sharp and unambiguous\n"
        + "  0.8  legible, with one or two characters you had to resolve from context\n"
        + "  0.5  partly obscured, blurred, glared or cropped — a human should check it\n"
        + "  0.0  not present, not legible, or hidden from you (value must be null)\n"
        + "Do not report high confidence for a value you inferred rather than read.\n"
        + "\n"
        + "Dates must be returned as yyyy-MM-dd. Sri Lankan documents print dates day-first.\n";

    /// <summary>The prompt for one document, listing exactly the keys its kind and side can carry.</summary>
    /// <param name="redacted">
    /// Whether the D-36 pre-pass actually ran on the image this prompt accompanies — the caller's
    /// fact, never a setting. It picks between <see cref="RedactedNotice"/> and
    /// <see cref="UnredactedNotice"/>; see the remarks on this class for why getting it wrong
    /// costs fields in both directions.
    /// </param>
    public static string For(string kind, string? side, bool redacted)
    {
        var keys = DocumentFieldKeys.AcceptedFor(kind, side);

        return Opening + (redacted ? RedactedNotice : UnredactedNotice) + Body + Classification
            // Δ MCS-21 — "expects this to be", not "this is". The sentence that follows is the
            // caller's claim, and the model has just been asked to judge it; asserting it as fact
            // is what made `document_type` an unanswerable question.
            + "\nThe caller expects the following. Say what you actually see, not this:\n"
            + Describe(kind, side) + "\n\nKeys to return:\n"
            + string.Join("\n", keys.Select(key => $"  {key} — {Explain(key)}"));
    }

    private static string Describe(string kind, string? side) => (kind, side) switch
    {
        // Δ MCS-17 — the alphabet comes from FieldValues.LicenceClasses, which is also what the
        // normaliser filters against. Written twice, the two drift, and the copy that drifts is
        // whichever one nothing reads back.
        (DocumentKinds.DrivingLicense, DocumentSides.Back) =>
            "This is the REVERSE of a Sri Lankan driving licence. It carries the table of licence "
            + $"classes ({string.Join(", ", FieldValues.LicenceClasses)}) the holder is entitled to drive. "
            + "The table has one row per class: column 9 is the class, column 10 its date of issue "
            + "and column 11 its date of expiry (Δ MCS-20).",
        (DocumentKinds.DrivingLicense, _) =>
            "This is the FRONT of a Sri Lankan driving licence. The date printed as 4a is the date "
            + "of ISSUE. The date of expiry is NOT on this side — it is column 11 of the class "
            + "table on the reverse, and is not asked for here (Δ MCS-20).",
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
        // Δ MCS-20 — WHICH date, and which of the table rows. "the date of expiry of the licence"
        // gave the model no anchor at all on a card that prints several dates, and it confidently
        // returned `4a` — the date of ISSUE — which then became registry.documents.expires_at and
        // the input to the E-03 suspension sweep.
        //
        // EARLIEST across the classes, and that is a decision rather than a reading: each class row
        // carries its own expiry, so a licence is only wholly valid until the FIRST of them lapses.
        // Taking the latest would keep the platform trusting a licence whose class B had already
        // expired because its class G1 had not.
        DocumentFieldKeys.LicenceExpiry =>
            "the date of expiry, from COLUMN 11 of the class table on the reverse. Each row of that "
            + "table is one licence class with its own date of issue (column 10) and date of expiry "
            + "(column 11). If the rows carry different expiry dates, return the EARLIEST of them. "
            + "Do NOT return the date printed as 4a on the front of the card — that is the date of "
            + "ISSUE, not the expiry",
        // Deliberately not "expect null": that read as an instruction rather than as a warning, and
        // it is only ever true on the redacted path, which the notice above already states (MCS-07).
        DocumentFieldKeys.NicNo => "the holder's national identity card number",
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
