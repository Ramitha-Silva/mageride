package lk.mageride.driver.vehicle

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.HourglassEmpty
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.WarningAmber
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.MageRideCtaTonal
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.StepVerdict
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-006 · vehicle onboarding status** — the four-document verdict list.
 *
 * The wireframe top to bottom: the ⏳ avatar with *"Sedan · ABC-1234"* and
 * *"Gemini Flash 3.0 verifying…"* beside it, the `Document verification (4)` card with one
 * Verified/Pending row per document, the amber banner counting what is pending, and the note that
 * all four Verified auto-approves the vehicle with no manual step.
 *
 * @param onDone Leaves for My Vehicles — where an approved vehicle now appears (SCR-DA-026).
 * @param onResume Opens the wizard again at whatever step is still outstanding (AL-30).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun VehicleOnboardingStatusScreen(onDone: () -> Unit, onResume: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: VehicleOnboardingStatusViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = {
                    Text(
                        text = stringResource(
                            if (state.isApproved) {
                                R.string.vehicle_status_approved_title
                            } else {
                                R.string.vehicle_status_title
                            },
                        ),
                    )
                },
                navigationIcon = {
                    IconButton(onClick = onDone) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.action_back),
                        )
                    }
                },
                actions = {
                    // A Pending document is confirmed by an officer minutes or days later, and
                    // US-2.14's push is what brings the driver back. This is how they look again.
                    IconButton(onClick = viewModel::refresh, enabled = !state.loading) {
                        Icon(
                            imageVector = Icons.Outlined.Refresh,
                            contentDescription = stringResource(R.string.action_retry),
                        )
                    }
                },
            )
        },
    ) { insets ->
        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            when {
                state.loading && state.verdicts == null -> Box(
                    modifier = Modifier.fillMaxWidth(),
                    contentAlignment = Alignment.Center,
                ) { CircularProgressIndicator() }

                state.unknownVehicle -> Text(
                    text = stringResource(R.string.vehicle_status_unknown),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )

                else -> {
                    VehicleHeader(state = state)
                    VerdictCard(state = state)

                    if (state.pendingCount > 0) {
                        NoticeCard(accent = MageRideTheme.status.warning, icon = Icons.Outlined.WarningAmber) {
                            Text(
                                text = stringResource(R.string.vehicle_status_pending_banner, state.pendingCount),
                                style = MaterialTheme.typography.bodyMedium,
                            )
                        }
                    }

                    state.rejectionReason?.takeIf { state.isRejected }?.let { reason ->
                        NoticeCard(
                            accent = MaterialTheme.colorScheme.error,
                            icon = Icons.Outlined.WarningAmber,
                            title = stringResource(R.string.vehicle_status_rejected_title),
                        ) {
                            Text(text = reason, style = MaterialTheme.typography.bodyMedium)
                        }
                    }

                    NoticeCard(accent = MageRideTheme.status.success, icon = Icons.Outlined.Info) {
                        Text(
                            text = stringResource(R.string.vehicle_status_auto_approve),
                            style = MaterialTheme.typography.bodyMedium,
                        )
                    }
                }
            }

            state.error?.let { message ->
                Text(
                    text = stringResource(message),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.error,
                )
            }

            Box(modifier = Modifier.weight(1f, fill = false))

            // A step that is not yet *saved* is the driver's to finish; one that is saved and
            // pending is an officer's. Only the first gets a Resume (AL-30, US-2.10).
            if (state.rows.any { (_, verdict) -> verdict == StepVerdict.PENDING_INPUT }) {
                MageRideCta(label = stringResource(R.string.vehicle_status_resume), onClick = onResume)
            }
            MageRideCtaTonal(label = stringResource(R.string.vehicle_status_my_vehicles), onClick = onDone)
        }
    }
}

/** The wireframe's ⏳ avatar, the *"Sedan · ABC-1234"* line, and the verifying caption under it. */
@Composable
private fun VehicleHeader(state: VehicleOnboardingStatusState, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val approved = state.isApproved
        Box(
            modifier = Modifier
                .size(ControlTokens.StatusAvatar)
                .background(
                    color = (if (approved) MageRideTheme.status.success else MageRideTheme.status.warning)
                        .copy(alpha = AVATAR_TINT),
                    shape = CircleShape,
                ),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = if (approved) Icons.Outlined.CheckCircle else Icons.Outlined.HourglassEmpty,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.IllustrationIcon),
                tint = if (approved) MageRideTheme.status.success else MageRideTheme.status.warning,
            )
        }
        Column(verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs)) {
            Text(
                text = listOfNotNull(
                    state.vehicleType?.let { stringResource(it.labelRes()) },
                    state.registrationNumber,
                ).joinToString(separator = " · "),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = stringResource(
                    if (approved) R.string.vehicle_status_approved_caption else R.string.vehicle_status_verifying,
                ),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** The wireframe's `Document verification (4)` card — one row per document, Verified or Pending. */
@Composable
private fun VerdictCard(state: VehicleOnboardingStatusState, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        elevation = CardDefaults.cardElevation(defaultElevation = MageRideTheme.elevation.level0),
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = stringResource(R.string.vehicle_status_documents, state.rows.size),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            state.rows.forEach { (step, verdict) ->
                Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = stringResource(step.documentRes()),
                        modifier = Modifier.weight(1f),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                    StatusPill(label = stringResource(verdict.labelRes()), tone = verdict.tone())
                }
            }
        }
    }
}

/** The document name each step stands for — the wireframe's four rows. */
private fun OnboardingStep.documentRes(): Int = when (this) {
    OnboardingStep.DETAILS -> R.string.vehicle_document_details
    OnboardingStep.INSURANCE -> R.string.vehicle_document_insurance
    OnboardingStep.REVENUE -> R.string.vehicle_document_revenue
    OnboardingStep.PHOTOS -> R.string.vehicle_document_photos
}

/**
 * The verdict as the driver reads it.
 *
 * `PENDING_INPUT` and `PENDING_REVIEW` are deliberately different copy: the first is *"not
 * uploaded"* and the driver can fix it, the second is *"an officer is looking at it"* and they
 * cannot. Collapsing the two into "Pending" would tell a driver to re-upload a document that is
 * already in the queue.
 */
private fun StepVerdict.labelRes(): Int = when (this) {
    StepVerdict.VERIFIED -> R.string.verdict_verified
    StepVerdict.PENDING_REVIEW -> R.string.verdict_pending_review
    StepVerdict.PENDING_INPUT -> R.string.verdict_pending_input
}

private fun StepVerdict.tone(): StatusTone = when (this) {
    StepVerdict.VERIFIED -> StatusTone.DONE
    StepVerdict.PENDING_REVIEW -> StatusTone.PENDING
    StepVerdict.PENDING_INPUT -> StatusTone.NEUTRAL
}

private const val AVATAR_TINT = 0.18f
