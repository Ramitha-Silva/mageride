package lk.mageride.shared.data.models

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Per-field verification state (`_shared.yaml#/components/schemas/VerifyStatus`, AL-29/AL-30).
 *
 * A [PENDING] field holds its vehicle in the Verification Officer queue and blocks approval
 * (US-2.10a). [AUTO_VERIFIED] and [CONFIRMED] both count as verified; the difference is who
 * decided — the confidence threshold or a human.
 *
 * **Δ MCS-15 — this enum was missing [AUTO_VERIFIED] and carried a `rejected` that does not
 * exist.** The contract is `enum: [auto_verified, pending, confirmed]` and registry-svc writes
 * `auto_verified` for every field extracted at or above `Registry:OcrConfidenceThreshold`. So the
 * moment extraction actually started working (MCS-07), the FIRST successful read of a licence
 * produced three `auto_verified` fields, [ExtractedField.verifyStatus] is non-nullable, and
 * kotlinx.serialization threw on the whole body — SCR-DA/DI-003a showed *"The app could not read
 * the reply from the server"* after a 200 that contained exactly the values the driver was
 * waiting to see. `rejected` was never emitted by anything and had no reader: the contract says a
 * rejection is recorded on the DOCUMENT (`registry.documents.status`), not on a field.
 *
 * This is the THIRD time this file has shipped an enum the wire does not use — see
 * [FieldSource]'s note on `ocr` vs `ai` (MCS-02), which is the same defect and was found the same
 * way, in production, by a driver. The test that closes the class is not another hand-written
 * table of members: it is an assertion against `backend/contracts/_shared.yaml` itself.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class VerifyStatus(public val wire: String) {
    /**
     * Extracted at or above the confidence threshold. Nobody has to look at it (D5' §14.1a).
     *
     * The default verdict for a clean scan, and therefore the one a working deployment produces
     * most of — which is why its absence was invisible until extraction worked.
     */
    @SerialName("auto_verified")
    AUTO_VERIFIED("auto_verified"),

    @SerialName("pending")
    PENDING("pending"),

    @SerialName("confirmed")
    CONFIRMED("confirmed"),
}

/**
 * Where a field's value came from (`_shared.yaml#/components/schemas/FieldSource`, AL-29).
 *
 * Anything the driver typed because a scan was unclear lands `manual` / `pending` and routes to
 * the officer queue (US-2.4a) — the distinction is the whole point of the field.
 *
 * **The extracted value is `ai`, not `ocr`.** `registry.document_fields.source` is
 * `CHECK (source IN ('ai','manual'))` (D4' §2) and `_shared.yaml` spells the enum `[ai, manual]`;
 * this enum said `ocr` until MCS-02, which meant a real registry-svc response failed to
 * deserialise on the one screen group that reads it. `ContractShapeTest` is what should have
 * caught it and could not — see the MCS-02 handoff.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class FieldSource(public val wire: String) {
    @SerialName("ai")
    AI("ai"),

    @SerialName("manual")
    MANUAL("manual"),
}

/**
 * One OCR-extracted or driver-entered onboarding field
 * (`_shared.yaml#/components/schemas/ExtractedField`, AL-29/AL-30).
 *
 * @property key Field name, e.g. `licenceNo`. A stable machine key, not display copy.
 * @property value The extracted or entered value; `null` when extraction found nothing.
 * @property source Whether OCR or the driver produced it.
 * @property confidence Gemini Flash 3.0 confidence, 0…1. Low confidence makes the field pending.
 * @property verifyStatus Where the field sits in the verification queue.
 */
@Serializable
public data class ExtractedField(
    val key: String,
    val value: String? = null,
    val source: FieldSource,
    val confidence: Double? = null,
    val verifyStatus: VerifyStatus,
)
