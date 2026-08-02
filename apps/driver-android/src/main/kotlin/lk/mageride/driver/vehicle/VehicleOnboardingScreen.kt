package lk.mageride.driver.vehicle

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.WarningAmber
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuAnchorType
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.capture.DocumentCaptureTarget
import lk.mageride.driver.ui.component.AdminVerifyChip
import lk.mageride.driver.ui.component.CaptureTile
import lk.mageride.driver.ui.component.ExtractedFieldRow
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.ModeCBadge
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.ExtractedField
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.registry.OnboardingStep
import org.koin.androidx.compose.koinViewModel
import kotlin.math.roundToInt

/**
 * **SCR-DA-004 → 004c · vehicle onboarding** — the optional Mode-C four-step wizard.
 *
 * One route and one composable for all four steps, because that is what they are: the wireframe
 * draws the same app bar, the same progress bar and the same CTA on each, with a different body
 * between them. Four destinations would put the resume rule (AL-30) in the navigation graph
 * instead of in [VehicleOnboardingViewModel], where it can be tested.
 *
 * **There is no permit field and no GPS-tracker field on any of the four.** Mode A/B vehicles and
 * route permits are onboarded in the Fleet Portal (AL-27, SCR-FP-004) and the wizard says so, in
 * the notice card the wireframe puts on Step 1/4.
 *
 * @param onCaptureRequested Opens SCR-DA-005 — the tiles here never photograph anything.
 * @param onSubmitted Step 4/4 is saved; hand over to SCR-DA-006.
 * @param onExit Back from Step 1/4 leaves the wizard entirely.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun VehicleOnboardingScreen(
    onCaptureRequested: () -> Unit,
    onSubmitted: () -> Unit,
    onExit: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: VehicleOnboardingViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.submitted) {
        if (state.submitted) onSubmitted()
    }
    LaunchedEffect(state.exited) {
        if (state.exited) onExit()
    }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(state.step.titleRes())) },
                navigationIcon = {
                    IconButton(onClick = viewModel::onBack) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.action_back),
                        )
                    }
                },
                actions = {
                    ModeCBadge(
                        label = stringResource(R.string.mode_c_badge),
                        modifier = Modifier.padding(end = MageRideTheme.spacing.sm),
                    )
                },
            )
        },
    ) { insets ->
        if (state.loading) {
            Box(
                modifier = Modifier
                    .padding(insets)
                    .fillMaxSize(),
                contentAlignment = Alignment.Center,
            ) {
                CircularProgressIndicator()
            }
            return@Scaffold
        }

        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            LinearProgressIndicator(progress = { state.progress }, modifier = Modifier.fillMaxWidth())
            Text(
                text = stringResource(
                    R.string.vehicle_onboard_step_of,
                    state.stepNumber,
                    OnboardingStep.entries.size,
                    stringResource(state.step.captionRes()),
                ),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            when (state.step) {
                OnboardingStep.DETAILS -> DetailsStep(state = state, viewModel = viewModel)

                OnboardingStep.INSURANCE -> DocumentStep(
                    state = state,
                    target = DocumentCaptureTarget.INSURANCE,
                    slotLabel = stringResource(R.string.vehicle_onboard_insurance_slot),
                    onCapture = {
                        viewModel.requestCapture(DocumentCaptureTarget.INSURANCE)
                        onCaptureRequested()
                    },
                )

                OnboardingStep.REVENUE -> DocumentStep(
                    state = state,
                    target = DocumentCaptureTarget.REVENUE_LICENCE,
                    slotLabel = stringResource(R.string.vehicle_onboard_revenue_slot),
                    onCapture = {
                        viewModel.requestCapture(DocumentCaptureTarget.REVENUE_LICENCE)
                        onCaptureRequested()
                    },
                )

                OnboardingStep.PHOTOS -> PhotosStep(
                    state = state,
                    onCapture = { target ->
                        viewModel.requestCapture(target)
                        onCaptureRequested()
                    },
                )
            }

            if (state.isPendingReview) {
                PendingReviewCard()
            }

            state.error?.let { message ->
                Text(
                    text = stringResource(message),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.error,
                )
            }

            MageRideCta(
                label = stringResource(state.step.ctaRes()),
                enabled = state.canContinue,
                loading = state.busy,
                onClick = viewModel::onContinue,
                modifier = Modifier.padding(top = MageRideTheme.spacing.xs),
            )
        }
    }
}

/**
 * **SCR-DA-004 · Step 1/4** — vehicle type and registration number, and nothing else.
 *
 * The notice card is not decoration. It is the one place a Mode-C driver is told where a permit
 * and a Mode A/B vehicle go, and the absence of those two fields is the AL-27 fence itself.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun DetailsStep(
    state: VehicleOnboardingState,
    viewModel: VehicleOnboardingViewModel,
    modifier: Modifier = Modifier,
) {
    var expanded by remember { mutableStateOf(false) }

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        ExposedDropdownMenuBox(expanded = expanded, onExpandedChange = { expanded = it }) {
            OutlinedTextField(
                value = state.vehicleType?.let { stringResource(it.labelRes()) }.orEmpty(),
                onValueChange = {},
                modifier = Modifier
                    .fillMaxWidth()
                    // `PrimaryNotEditable` — the field is the menu's anchor and is read-only, so
                    // TalkBack announces a dropdown rather than a text box that will not accept
                    // typing. The no-argument `menuAnchor()` is deprecated in M3 1.4.
                    .menuAnchor(ExposedDropdownMenuAnchorType.PrimaryNotEditable),
                readOnly = true,
                label = { Text(text = stringResource(R.string.vehicle_onboard_type_label)) },
                trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
            )
            ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                // The Mode-C subset (AL-09). `bus` and `train` are Mode A and are not offered —
                // `POST /v1/vehicles` answers 403 mode-not-allowed for either.
                RideVehicleType.entries.forEach { type ->
                    DropdownMenuItem(
                        text = { Text(text = stringResource(type.labelRes())) },
                        onClick = {
                            viewModel.onVehicleTypeChanged(type)
                            expanded = false
                        },
                    )
                }
            }
        }

        OutlinedTextField(
            value = state.registrationNumber,
            onValueChange = viewModel::onRegistrationChanged,
            modifier = Modifier.fillMaxWidth(),
            label = { Text(text = stringResource(R.string.vehicle_onboard_registration_label)) },
            singleLine = true,
            isError = state.registrationTaken,
            supportingText = {
                if (state.registrationTaken) {
                    Text(
                        text = stringResource(R.string.error_registration_exists),
                        style = MaterialTheme.typography.labelSmall,
                    )
                }
            },
        )

        NoticeCard(
            accent = MageRideTheme.mode.modeC,
            icon = Icons.Outlined.Info,
            title = stringResource(R.string.vehicle_onboard_mode_c_title),
        ) {
            Text(
                text = stringResource(R.string.vehicle_onboard_mode_c_body),
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }
}

/**
 * **SCR-DA-004a / 004b · Steps 2 and 3** — one capture, one `Done ✓`, one extract card.
 *
 * The two steps differ only in what they capture and which fields come back, so they are one
 * composable. The extract card appears only after a save, because the save is what queues the
 * Gemini Flash extraction — see [VehicleOnboardingViewModel].
 */
@Composable
private fun DocumentStep(
    state: VehicleOnboardingState,
    target: DocumentCaptureTarget,
    slotLabel: String,
    onCapture: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        CaptureTile(
            label = slotLabel,
            captureHint = stringResource(R.string.action_tap_to_capture),
            doneLabel = stringResource(R.string.status_done),
            captured = state.captured(target),
            onClick = onCapture,
            height = ControlTokens.CapturePanel,
        )

        SlotStatusRow(label = slotLabel, done = state.captured(target))
        ExtractionCard(fields = state.stepFields)
    }
}

/**
 * **SCR-DA-004c · Step 4/4** — front and back, number plate visible on both.
 *
 * The notice under the tiles is the step's whole rule: the plate read out of these photos is
 * matched against the registration number typed on Step 1/4, and a mismatch is what makes the step
 * Pending (BR-25.3, US-2.27).
 */
@Composable
private fun PhotosStep(
    state: VehicleOnboardingState,
    onCapture: (DocumentCaptureTarget) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
            CaptureTile(
                label = stringResource(R.string.vehicle_onboard_photo_front),
                captureHint = stringResource(R.string.action_tap_to_capture),
                doneLabel = stringResource(R.string.status_done),
                captured = state.photoFront != null,
                onClick = { onCapture(DocumentCaptureTarget.VEHICLE_FRONT) },
                modifier = Modifier.weight(1f),
                height = ControlTokens.CapturePanel,
            )
            CaptureTile(
                label = stringResource(R.string.vehicle_onboard_photo_back),
                captureHint = stringResource(R.string.action_tap_to_capture),
                doneLabel = stringResource(R.string.status_done),
                captured = state.photoBack != null,
                onClick = { onCapture(DocumentCaptureTarget.VEHICLE_BACK) },
                modifier = Modifier.weight(1f),
                height = ControlTokens.CapturePanel,
            )
        }

        SlotStatusRow(
            label = stringResource(R.string.vehicle_onboard_photos_slot),
            done = state.photoFront != null && state.photoBack != null,
        )

        NoticeCard(accent = MageRideTheme.mode.modeC, icon = Icons.Outlined.Info) {
            Text(
                text = stringResource(R.string.vehicle_onboard_plate_match, state.registrationNumber),
                style = MaterialTheme.typography.bodyMedium,
            )
        }

        ExtractionCard(fields = state.stepFields)
    }
}

/** The wireframe's `🛡 Insurance document — Done ✓` row under a capture panel. */
@Composable
private fun SlotStatusRow(label: String, done: Boolean, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = label,
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
        StatusPill(
            label = stringResource(if (done) R.string.status_done else R.string.status_not_captured),
            tone = if (done) StatusTone.DONE else StatusTone.NEUTRAL,
        )
    }
}

/**
 * The wireframe's *"✦ AI-extracted (Gemini Flash 3.0)"* card.
 *
 * **Every row is read-only, and the wireframe draws a ✎ on some of them.** The multipart arm of
 * `PUT /v1/vehicles/{id}/onboarding/{step}` carries `registrationNumber`, `vehicleType`, `file`
 * and `fileBack` — it has **no `fields` part** — and the JSON arm that does needs a `fileId` no
 * response on this surface ever returns. So a correction typed here would have nowhere to go and
 * would be silently dropped on save. Showing the verdicts with the ⚑ chip is the honest
 * rendering, and it is not a dead end: a doubtful field is `pending_review` and goes to the
 * Verification Officer, which is exactly what BR-25.3 asks for. Raised as a micro-change-set in
 * the C069 handoff; same shape as C068's finding on `licence_no` / `licence_expiry`.
 */
@Composable
private fun ExtractionCard(fields: List<ExtractedField>, modifier: Modifier = Modifier) {
    if (fields.isEmpty()) return

    NoticeCard(
        accent = MageRideTheme.status.success,
        icon = Icons.Outlined.AutoAwesome,
        title = stringResource(R.string.vehicle_onboard_extract_title),
        modifier = modifier,
    ) {
        fields.forEach { field ->
            ExtractedFieldRow(
                label = stringResource(field.labelRes()),
                value = field.displayValue(),
                emptyLabel = stringResource(R.string.profile_setup_field_unread),
                flagLabel = stringResource(R.string.flag_admin_verify).takeIf { field.needsOfficerReview },
            )
        }

        // The wireframe's `Confidence · 0.62 — doubtful` row. The **lowest** of the step's fields,
        // because a step is only as verified as its weakest one (BR-25.3), and "doubtful" is read
        // off the server's own `verifyStatus` rather than compared against a threshold here —
        // `Registry:OcrConfidenceThreshold` is admin-tunable and a second copy of it in the app
        // would eventually disagree with the verdict printed beside it.
        fields.lowestConfidence()?.let { confidence ->
            ExtractedFieldRow(
                label = stringResource(R.string.vehicle_field_confidence),
                value = if (fields.any { it.needsOfficerReview }) {
                    stringResource(R.string.vehicle_confidence_doubtful, confidence.formatted())
                } else {
                    confidence.formatted()
                },
                emptyLabel = stringResource(R.string.profile_setup_field_unread),
            )
        }
    }
}

/** The weakest extraction in this step, or `null` when nothing came back with a confidence. */
private fun List<ExtractedField>.lowestConfidence(): Double? = mapNotNull(ExtractedField::confidence).minOrNull()

/** `0.62`. Two decimals, built by arithmetic so the decimal separator is the same in all three locales. */
private fun Double.formatted(): String {
    val hundredths = (this * PERCENT).roundToInt()
    return "${hundredths / PERCENT}.${(hundredths % PERCENT).toString().padStart(2, '0')}"
}

private const val PERCENT = 100

/** The wireframe's amber *"Any element doubtful or edited → this step's status is Pending"* card. */
@Composable
private fun PendingReviewCard(modifier: Modifier = Modifier) {
    NoticeCard(
        accent = MageRideTheme.status.warning,
        icon = Icons.Outlined.WarningAmber,
        title = stringResource(R.string.vehicle_onboard_pending_title),
        modifier = modifier,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = stringResource(R.string.vehicle_onboard_pending_body),
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodyMedium,
            )
            AdminVerifyChip(label = stringResource(R.string.flag_admin_verify))
        }
    }
}
