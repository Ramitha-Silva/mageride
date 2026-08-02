package lk.mageride.driver.onboarding

import lk.mageride.driver.capture.CapturedImage
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.models.ExtractedField
import lk.mageride.shared.data.models.FieldSource
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.VerifyStatus
import lk.mageride.shared.data.models.iam.UserProfile

/**
 * The four licence fields Gemini Flash extracts, as the keys `registry.document_fields` stores
 * them (AL-29; `DocumentFieldKeys` in registry-svc).
 *
 * Machine keys, never display copy — the row labels are `strings.xml`'s and are trilingual.
 */
internal object LicenceFieldKeys {
    const val LICENCE_NO = "licence_no"
    const val LICENCE_EXPIRY = "licence_expiry"
    const val NIC_NO = "nic_no"
    const val ALLOWED_VEHICLE_TYPES = "allowed_vehicle_types"

    /** The order SCR-DA-003a's extract card lists them in. */
    val Order: List<String> = listOf(LICENCE_NO, LICENCE_EXPIRY, NIC_NO, ALLOWED_VEHICLE_TYPES)
}

/**
 * What the driver has filled in on SCR-DA-003a.
 *
 * @property name `registry.driver_profiles.display_name`, at most 200 characters.
 * @property photo Required (US-2.12) — a passenger has to be able to recognise the driver.
 * @property licenceFront Front of the driving licence, captured through SCR-DA-005.
 * @property licenceBack Back of the same licence.
 * @property nicNo Typed only when the scan was unclear; goes up as `source='manual'` (AL-29).
 * @property allowedVehicleTypes Same, for the licence classes.
 */
internal data class ProfileDraft(
    val name: String = "",
    val photo: CapturedImage? = null,
    val licenceFront: CapturedImage? = null,
    val licenceBack: CapturedImage? = null,
    val nicNo: String? = null,
    val allowedVehicleTypes: List<VehicleType>? = null,
) {
    /** Whether Save is allowed: name, photo and both licence sides are all required (AL-27). */
    val isComplete: Boolean
        get() = name.isNotBlank() && photo != null && licenceFront != null && licenceBack != null

    /** Whether the driver typed a value the scan should have supplied — the AL-29 manual path. */
    val hasManualEntry: Boolean
        get() = !nicNo.isNullOrBlank() || !allowedVehicleTypes.isNullOrEmpty()

    /**
     * Just the two driver-typed values, for comparing one save against the next.
     *
     * The images are deliberately not part of it: `CapturedImage` compares by identity, and what
     * the second CTA tap needs to know is whether a **correction** was typed, not whether the
     * same photograph is still in memory.
     */
    val manualValues: Pair<String?, List<VehicleType>?>
        get() = nicNo?.takeIf(String::isNotBlank) to allowedVehicleTypes?.takeIf(List<VehicleType>::isNotEmpty)
}

/**
 * One row of SCR-DA-003a's "✦ AI-extracted from licence" card.
 *
 * @property key The `registry.document_fields` key, e.g. `nic_no`.
 * @property value What was read or typed; `null` when extraction found nothing.
 * @property source Whether OCR or the driver produced it (AL-29).
 * @property needsOfficerReview Whether this field is flagged ⚑ for a Verification Officer.
 */
internal data class LicenceField(
    val key: String,
    val value: String?,
    val source: FieldSource,
    val needsOfficerReview: Boolean,
) {
    /** Whether the driver typed this one, which is what the ⚑ chip's copy explains. */
    val isManual: Boolean get() = source == FieldSource.MANUAL
}

/**
 * The verdict SCR-DA-003a shows after a save.
 *
 * @property fields The four licence rows, in [LicenceFieldKeys.Order].
 * @property displayName The name registry-svc stored, which is not always the one that was sent.
 */
internal data class LicenceExtraction(val fields: List<LicenceField>, val displayName: String?) {

    /**
     * Whether the driver has to look at this before continuing.
     *
     * BR-25.2: a field is pending when it is `manual` **or** low confidence, and a required field
     * that extraction never returned comes back with a null value. Either way the card is the
     * point of the screen and skipping past it would hide the ⚑.
     */
    val needsReview: Boolean get() = fields.any { it.needsOfficerReview || it.value.isNullOrBlank() }

    /** Whether any field is flagged for the Verification Officer queue (SCR-AP-003). */
    val hasOfficerFlag: Boolean get() = fields.any { it.needsOfficerReview }

    /** The row for one key, or `null` when the server did not return it. */
    fun field(key: String): LicenceField? = fields.firstOrNull { it.key == key }
}

/**
 * SCR-DA-003a's data: upload the three images, `PUT /v1/drivers/profile`, read back the verdicts.
 *
 * **Profile Setup is driver identity and nothing else** (AL-27, Change 6/22). It writes
 * `registry.driver_profiles` plus a **vehicle-less** `registry.documents(kind='driving_license')`
 * and it precedes Home; no vehicle is involved, and the Mode-C wizard that onboards one is
 * optional and belongs to C069.
 */
internal class DriverProfileRepository(private val registry: RegistryApi, private val iam: IamApi) {

    /** `GET /v1/users/me` — the splash router's input for "has this driver a profile yet?". */
    suspend fun me(): UserProfile = iam.getMyProfile()

    /**
     * `PUT /v1/drivers/profile` — the three images and the name, in one request.
     *
     * The multipart arm (Δ MCS-01): registry-svc writes each image to `docs.uploads` and the
     * profile row in the same call, so there is no id for a client to mint and nothing to hold
     * between two requests on a mobile network. Each image carries its own capture source (AL-43)
     * — the avatar came from the picker, the licence from SCR-DA-005 — and that provenance is
     * what the Verification-Officer queue sorts on.
     *
     * A driver-supplied [ProfileDraft.nicNo] or [ProfileDraft.allowedVehicleTypes] is sent as-is:
     * **registry-svc** is what stamps it `source='manual'`, `verify_status='pending'` and queues
     * the officer review (AL-29, US-2.4a). The client never claims a provenance of its own.
     */
    suspend fun submit(draft: ProfileDraft): LicenceExtraction {
        require(draft.isComplete) { "Profile Setup needs a name, a photo and both licence sides" }

        val response = registry.uploadDriverProfile(
            driverName = draft.name.trim(),
            photo = requireNotNull(draft.photo).asDocument(),
            licenseFront = requireNotNull(draft.licenceFront).asDocument(),
            licenseBack = requireNotNull(draft.licenceBack).asDocument(),
            nicNo = draft.nicNo?.takeIf(String::isNotBlank),
            allowedVehicleTypes = draft.allowedVehicleTypes?.takeIf(List<VehicleType>::isNotEmpty),
        )

        return extractionOf(response.fields, draft.name.trim())
    }

    private fun extractionOf(fields: List<ExtractedField>, fallbackName: String): LicenceExtraction {
        val byKey = fields.associateBy(ExtractedField::key)
        val rows = LicenceFieldKeys.Order.map { key ->
            val field = byKey[key]
            LicenceField(
                key = key,
                value = field?.value,
                source = field?.source ?: FieldSource.AI,
                // A key the server did not answer for at all has not been verified either; the
                // screen shows it as unread, which is the same prompt to type it in.
                needsOfficerReview = field == null || field.verifyStatus == VerifyStatus.PENDING,
            )
        }
        return LicenceExtraction(fields = rows, displayName = fallbackName)
    }
}
