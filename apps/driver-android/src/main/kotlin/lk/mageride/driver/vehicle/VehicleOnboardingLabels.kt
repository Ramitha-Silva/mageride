package lk.mageride.driver.vehicle

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.shared.data.models.ExtractedField
import lk.mageride.shared.data.models.registry.OnboardingStep

// The wizard's copy table — which string resource each step and each extracted field renders as.
//
// Split from the screen because it is a TABLE, not a layout: five small mappings between a machine
// key and a trilingual label, all of which a later component (the iOS SCR-DI-004 mirror, the
// Verification-Officer view of the same fields) will want to read without opening a Compose file.

/** The app-bar title for each step — the wireframe's four. */
internal fun OnboardingStep.titleRes(): Int = when (this) {
    OnboardingStep.DETAILS -> R.string.vehicle_onboard_title
    OnboardingStep.INSURANCE -> R.string.vehicle_onboard_insurance_title
    OnboardingStep.REVENUE -> R.string.vehicle_onboard_revenue_title
    OnboardingStep.PHOTOS -> R.string.vehicle_onboard_photos_title
}

/** The line under the progress bar — *"Step 2 of 4 · Insurance card or paper"*. */
internal fun OnboardingStep.captionRes(): Int = when (this) {
    OnboardingStep.DETAILS -> R.string.vehicle_onboard_details_caption
    OnboardingStep.INSURANCE -> R.string.vehicle_onboard_insurance_caption
    OnboardingStep.REVENUE -> R.string.vehicle_onboard_revenue_caption
    OnboardingStep.PHOTOS -> R.string.vehicle_onboard_photos_caption
}

/** The CTA label. Step 4/4's is `succ`-coloured in the wireframe and says *submit for review*. */
internal fun OnboardingStep.ctaRes(): Int = when (this) {
    OnboardingStep.DETAILS -> R.string.vehicle_onboard_cta_details
    OnboardingStep.INSURANCE -> R.string.vehicle_onboard_cta_insurance
    OnboardingStep.REVENUE -> R.string.vehicle_onboard_cta_revenue
    OnboardingStep.PHOTOS -> R.string.vehicle_onboard_cta_submit
}

/** The trilingual row label for one `registry.document_fields` key. */
internal fun ExtractedField.labelRes(): Int = when (key) {
    VehicleFieldKeys.INSURANCE_EXPIRY, VehicleFieldKeys.REVENUE_EXPIRY -> R.string.profile_setup_field_expiry
    VehicleFieldKeys.INSURANCE_POLICY_NO -> R.string.vehicle_field_policy_no
    VehicleFieldKeys.INSURER -> R.string.vehicle_field_insurer
    VehicleFieldKeys.REVENUE_NO -> R.string.vehicle_field_revenue_no
    VehicleFieldKeys.PLATE_TEXT -> R.string.vehicle_field_plate_text
    else -> R.string.vehicle_field_plate_match
}

/**
 * What the row shows.
 *
 * `reg_no_match` is a boolean on the wire and *"true"* is not copy — it is the answer to "did the
 * plate in the photograph match the one you typed?", and that question has a trilingual yes and a
 * trilingual no. Every other key carries a value read off the document and is shown as read.
 */
@Composable
internal fun ExtractedField.displayValue(): String? = if (key == VehicleFieldKeys.REG_NO_MATCH) {
    value?.let { matched ->
        stringResource(
            if (matched.equals("true", ignoreCase = true)) {
                R.string.vehicle_plate_matched
            } else {
                R.string.vehicle_plate_mismatch
            },
        )
    }
} else {
    value
}
