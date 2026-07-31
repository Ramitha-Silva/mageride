using MageRide.Ocr.Domain;

namespace MageRide.Ocr.Pipeline;

/// <summary>
/// The field-level verdict — C054's third fence, and the only verdict this service makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule, applied to fields.</b> A doubtful read, a field that did not extract at all and a
/// plate that did not match all arrive here as the same thing: a field that is
/// <c>pending</c>. That is C029's rule (3) stated from this side of the seam, and it is why there
/// is one clause rather than four that can drift apart. Whether the owning onboarding <em>step</em>
/// is <c>pending_review</c> and whether the vehicle reaches <c>APPROVED</c> are registry-svc's
/// (AL-30) — properties of tables this service does not own.
/// </para>
/// <para>
/// <b>An unscored field is a doubtful field.</b> A value with no confidence has not been verified,
/// whatever produced it — the same rule registry-svc applies, deliberately, so the two services
/// cannot reach different conclusions about the same document.
/// </para>
/// </remarks>
public static class FieldVerdicts
{
    /// <summary>Whether a field needs a Verification Officer (SCR-AP-003).</summary>
    public static string For(string key, string? value, decimal? confidence, decimal threshold) =>
        IsPending(key, value, confidence, threshold) ? VerifyStatuses.Pending : VerifyStatuses.AutoVerified;

    private static bool IsPending(string key, string? value, decimal? confidence, decimal threshold)
    {
        // Nothing was read. The key is emitted anyway so the officer queue shows a row to fill
        // rather than an absence to notice.
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // D5' §14.1a's photos row, and it fails in both directions. A model that read a different
        // vehicle's plate perfectly is a document nobody may auto-approve on however sure it was —
        // so a confident mismatch is still pending. And a *match* is only as good as the read
        // behind it: agreeing with the registration on a plate nobody could make out is not
        // evidence that the photograph is of this vehicle, so the confidence still applies.
        if (key == DocumentFieldKeys.RegNoMatch)
        {
            return !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || confidence is null
                || confidence < threshold;
        }

        return confidence is null || confidence < threshold;
    }

    /// <summary>
    /// Applies the verdict to a field, and normalises its value on the way through.
    /// </summary>
    public static ExtractedField Judge(ExtractedField field, decimal threshold)
    {
        ArgumentNullException.ThrowIfNull(field);

        var value = field.Key == DocumentFieldKeys.RegNoMatch
            ? field.Value
            : FieldValues.Normalise(field.Key, field.Value);

        return field with
        {
            Value = value,
            VerifyStatus = For(field.Key, value, field.Confidence, threshold),
            Source = FieldSources.Ai,
        };
    }
}
