package lk.mageride.driver.onboarding

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
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
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.PhotoCamera
import androidx.compose.material.icons.outlined.WarningAmber
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.capture.CapturedImage
import lk.mageride.driver.capture.DocumentCaptureTarget
import lk.mageride.driver.capture.rememberCapturedBitmap
import lk.mageride.driver.ui.component.AdminVerifyChip
import lk.mageride.driver.ui.component.CaptureTile
import lk.mageride.driver.ui.component.ExtractedFieldRow
import lk.mageride.driver.ui.component.LabelledTextField
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.labelRes
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.VehicleType
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-003a · profile setup** — driver identity, and the last screen before Home.
 *
 * The wireframe top to bottom: an elevated "Set up your profile" bar, the centred avatar with its
 * camera badge and the *required* caption under it, the driver-name field, the two licence capture
 * tiles, the "✦ AI-extracted from licence" card, the ⚑ manual-entry warning, and the
 * "Save & continue" CTA.
 *
 * Two fences hold this screen up:
 *
 * * **No vehicle** (AL-27). Nothing here asks for one, and the next screen is SCR-DA-007, not the
 *   Mode-C wizard.
 * * **The scanner is SCR-DA-005's** (AL-43, C069). A capture tile opens it; it does not photograph
 *   anything itself. **The profile photo goes through it too**, on the selfie lens and with no crop
 *   box — see `DocumentCaptureTarget.isDocument`. It used to be a gallery pick, which meant the
 *   photograph *"shown to passengers"* (US-2.12) could be any file on the handset and never a
 *   picture of the driver holding it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ProfileSetupScreen(
    onCaptureRequested: () -> Unit,
    onComplete: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: ProfileSetupViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    LaunchedEffect(state.done) {
        if (state.done) onComplete()
    }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = { TopAppBar(title = { Text(text = stringResource(R.string.profile_setup_title)) }) },
    ) { insets ->
        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            ProfilePhoto(
                photo = state.draft.photo,
                onCapture = {
                    viewModel.requestCapture(DocumentCaptureTarget.PROFILE_PHOTO)
                    onCaptureRequested()
                },
            )

            LabelledTextField(
                label = stringResource(R.string.profile_setup_name_label),
                value = state.draft.name,
                onValueChange = viewModel::onNameChanged,
            )

            SectionLabel(text = stringResource(R.string.profile_setup_licence_label))
            Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                CaptureTile(
                    label = stringResource(R.string.profile_setup_licence_front),
                    captureHint = stringResource(R.string.action_tap_to_capture),
                    doneLabel = stringResource(R.string.status_done),
                    captured = state.draft.licenceFront != null,
                    onClick = {
                        viewModel.requestCapture(DocumentCaptureTarget.LICENCE_FRONT)
                        onCaptureRequested()
                    },
                    modifier = Modifier.weight(1f),
                )
                CaptureTile(
                    label = stringResource(R.string.profile_setup_licence_back),
                    captureHint = stringResource(R.string.action_tap_to_capture),
                    doneLabel = stringResource(R.string.status_done),
                    captured = state.draft.licenceBack != null,
                    onClick = {
                        viewModel.requestCapture(DocumentCaptureTarget.LICENCE_BACK)
                        onCaptureRequested()
                    },
                    modifier = Modifier.weight(1f),
                )
            }

            state.extraction?.let { extraction ->
                ExtractionCard(
                    extraction = extraction,
                    draft = state.draft,
                    viewModel = viewModel,
                    editingKey = state.editingKey,
                )
            }

            if (state.hasOfficerFlag) {
                NoticeCard(
                    accent = MageRideTheme.status.warning,
                    icon = Icons.Outlined.WarningAmber,
                    title = stringResource(R.string.profile_setup_manual_title),
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = stringResource(R.string.profile_setup_manual_body),
                            modifier = Modifier.weight(1f),
                            style = MaterialTheme.typography.bodyMedium,
                        )
                        AdminVerifyChip(label = stringResource(R.string.flag_admin_verify))
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

            MageRideCta(
                label = stringResource(R.string.profile_setup_save),
                enabled = state.canSave,
                loading = state.busy,
                onClick = viewModel::save,
                modifier = Modifier.padding(top = MageRideTheme.spacing.xs),
            )
        }
    }
}

/**
 * The wireframe's avatar with the camera badge, and the caption that makes the requirement plain.
 *
 * *"Profile photo · required — shown to passengers"* (US-2.12). The word `required` carries the
 * error colour because it is the one field whose absence a driver will not otherwise notice — the
 * CTA simply stays dead.
 */
@Composable
private fun ProfilePhoto(photo: CapturedImage?, onCapture: () -> Unit, modifier: Modifier = Modifier) {
    val captured = photo != null
    val face = rememberCapturedBitmap(photo)

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(contentAlignment = Alignment.BottomEnd) {
            Box(
                modifier = Modifier
                    .size(ControlTokens.Avatar)
                    .clip(CircleShape)
                    .background(MaterialTheme.colorScheme.surfaceVariant, CircleShape)
                    .border(
                        width = if (captured) ControlTokens.BorderSelected else ControlTokens.Border,
                        color = if (captured) MageRideTheme.status.success else MaterialTheme.colorScheme.outline,
                        shape = CircleShape,
                    )
                    .clickable(onClick = onCapture),
                contentAlignment = Alignment.Center,
            ) {
                // The shot the driver has just taken, in the circle a passenger will see it in. A
                // green ring around the same anonymous silhouette said the capture had been
                // recorded without ever showing them what was recorded.
                if (face != null) {
                    Image(
                        bitmap = face,
                        contentDescription = null,
                        modifier = Modifier.fillMaxSize(),
                        // CROP, not FIT: the still is 4:3 and the avatar is a circle, so fitting it
                        // would letterbox the driver's face into the middle third of the ring.
                        contentScale = ContentScale.Crop,
                    )
                } else {
                    Icon(
                        imageVector = Icons.Outlined.Person,
                        contentDescription = null,
                        modifier = Modifier.size(ControlTokens.IllustrationIcon),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            Box(
                modifier = Modifier
                    .size(ControlTokens.AvatarBadge)
                    .background(MaterialTheme.colorScheme.primary, CircleShape)
                    .clickable(onClick = onCapture),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = Icons.Outlined.PhotoCamera,
                    contentDescription = stringResource(R.string.profile_setup_photo_action),
                    modifier = Modifier.size(ControlTokens.ChipIcon),
                    tint = MaterialTheme.colorScheme.onPrimary,
                )
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs)) {
            Text(
                text = stringResource(R.string.profile_setup_photo_caption),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )
            Text(
                text = stringResource(R.string.profile_setup_photo_required),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.error,
            )
        }
    }
}

/**
 * The wireframe's "✦ AI-extracted from licence (Gemini Flash · PII redacted)" card.
 *
 * **Δ MCS-02 — `licence_no` and `licence_expiry` are editable now.** The C068 handoff recorded
 * them as read-only because `PUT /v1/drivers/profile` carried parts for `nicNo` and
 * `allowedVehicleTypes` and nothing else, so an edit to either of the other two rows would have
 * been silently dropped on save. The multipart arm now carries them, which is what AL-29's manual
 * path was always for: the driver types what the scan could not read, and registry-svc — never
 * this screen — stamps it `source='manual'`, `verify_status='pending'`.
 */
@Composable
private fun ExtractionCard(
    extraction: LicenceExtraction,
    draft: ProfileDraft,
    viewModel: ProfileSetupViewModel,
    editingKey: String?,
    modifier: Modifier = Modifier,
) {
    NoticeCard(
        accent = MageRideTheme.status.success,
        icon = Icons.Outlined.AutoAwesome,
        title = stringResource(R.string.profile_setup_extract_title),
        modifier = modifier,
    ) {
        extraction.fields.forEach { field ->
            val editable = field.key == LicenceFieldKeys.LICENCE_NO || field.key == LicenceFieldKeys.LICENCE_EXPIRY
            val typed = when (field.key) {
                LicenceFieldKeys.LICENCE_NO -> draft.licenceNo
                LicenceFieldKeys.LICENCE_EXPIRY -> draft.licenceExpiry
                else -> null
            }

            ExtractedFieldRow(
                label = stringResource(field.fieldLabelRes()),
                value = typed ?: field.value,
                emptyLabel = stringResource(R.string.profile_setup_field_unread),
                flagLabel = stringResource(R.string.flag_admin_verify).takeIf { field.needsOfficerReview },
                editLabel = stringResource(R.string.action_edit).takeIf { editable },
                onEdit = if (editable) {
                    { viewModel.toggleEdit(field.key) }
                } else {
                    null
                },
            )

            if (editingKey == field.key) {
                LabelledTextField(
                    label = stringResource(field.fieldLabelRes()),
                    value = typed ?: field.value.orEmpty(),
                    onValueChange = { viewModel.onLicenceFieldChanged(field.key, it) },
                    supporting = stringResource(R.string.profile_setup_manual_supporting),
                )
            }
        }

        // The two values the contract lets a driver supply. They appear only when the scan did not
        // settle them, which is BR-25.2's "if a value is unreadable the driver types it".
        if (extraction.field(LicenceFieldKeys.NIC_NO)?.needsOfficerReview != false) {
            LabelledTextField(
                label = stringResource(R.string.profile_setup_nic_label),
                value = draft.nicNo.orEmpty(),
                onValueChange = viewModel::onNicChanged,
                supporting = stringResource(R.string.profile_setup_manual_supporting),
            )
        }
        if (extraction.field(LicenceFieldKeys.ALLOWED_VEHICLE_TYPES)?.needsOfficerReview != false) {
            SectionLabel(text = stringResource(R.string.profile_setup_allowed_types_label))
            AllowedVehicleTypePicker(
                selected = draft.allowedVehicleTypes.orEmpty(),
                onChange = viewModel::onAllowedVehicleTypesChanged,
            )
        }
    }
}

/**
 * The licence classes a driver types in when the scan could not read them.
 *
 * Chips over the **canonical vehicle types** (AL-09) rather than free text, because
 * `allowedVehicleTypes` is a `VehicleType[]` on the wire — a driver typing `B1` would produce a
 * value `400 validation-failed` rejects. `bus` and `train` are absent: they are Mode A, and Mode A
 * vehicles are the Fleet Portal's (AL-27).
 */
@Composable
private fun AllowedVehicleTypePicker(
    selected: List<VehicleType>,
    onChange: (List<VehicleType>) -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        RideVehicleType.entries.forEach { rideType ->
            val type = rideType.toVehicleType()
            val isSelected = type in selected
            FilterChip(
                selected = isSelected,
                onClick = {
                    onChange(if (isSelected) selected - type else selected + type)
                },
                label = { Text(text = stringResource(rideType.labelRes())) },
            )
        }
    }
}

/** The trilingual row label for one `registry.document_fields` key. */
private fun LicenceField.fieldLabelRes(): Int = when (key) {
    LicenceFieldKeys.LICENCE_NO -> R.string.profile_setup_field_licence_no
    LicenceFieldKeys.LICENCE_EXPIRY -> R.string.profile_setup_field_expiry
    LicenceFieldKeys.NIC_NO -> R.string.profile_setup_field_nic
    else -> R.string.profile_setup_field_allowed_types
}
