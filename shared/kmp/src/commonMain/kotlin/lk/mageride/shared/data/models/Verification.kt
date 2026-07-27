package lk.mageride.shared.data.models

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Per-field verification state (`_shared.yaml#/components/schemas/VerifyStatus`, AL-29/AL-30).
 *
 * A [PENDING] field holds its vehicle in the Verification Officer queue and blocks approval
 * (US-2.10a). When every field of every step is [CONFIRMED] the vehicle auto-approves with no
 * officer step at all (user decision 6/22).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class VerifyStatus(public val wire: String) {
    @SerialName("pending")
    PENDING("pending"),

    @SerialName("confirmed")
    CONFIRMED("confirmed"),

    @SerialName("rejected")
    REJECTED("rejected"),
}

/**
 * Where a field's value came from (`_shared.yaml#/components/schemas/FieldSource`, AL-29).
 *
 * Anything the driver typed because a scan was unclear lands `manual` / `pending` and routes to
 * the officer queue (US-2.4a) — the distinction is the whole point of the field.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class FieldSource(public val wire: String) {
    @SerialName("ocr")
    OCR("ocr"),

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
