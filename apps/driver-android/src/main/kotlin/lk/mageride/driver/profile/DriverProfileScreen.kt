package lk.mageride.driver.profile

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
import androidx.compose.material.icons.automirrored.outlined.KeyboardArrowRight
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.DirectionsCar
import androidx.compose.material.icons.outlined.Edit
import androidx.compose.material.icons.outlined.EmergencyShare
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.StarOutline
import androidx.compose.material.icons.outlined.WorkspacePremium
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.onboarding.endonym
import lk.mageride.driver.onboarding.findActivity
import lk.mageride.driver.ui.Symbols
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.DriverHeader
import lk.mageride.driver.ui.component.DriverHeaderState
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-029 · driver profile** (US-18.3, AL-13).
 *
 * The wireframe: a `‹ Profile` app bar, the identity card with its ✎, then **Vehicle details**,
 * **Per-trip ratings**, **Emergency contact** and **Driver Level** as list rows. The C074
 * deliverable adds three the sketch has no room for and D2' §SCR-DA-029 implies — the **language**,
 * the **notification switches** (US-10.7) and **log out**.
 *
 * @param onOpenVehicles *"Vehicle details ›"* — SCR-DA-026, which is where a driver's vehicles are.
 * @param onOpenRatings *"Per-trip ratings ›"* — SCR-DA-030, which is where the per-trip stars are
 *   (US-18.3). Not a screen of its own: the ratings live on the trips.
 * @param onOpenLevel *"Driver Level  L3 ›"* — SCR-DA-019.
 * @param onSignedOut The session has ended; the shell routes back through the splash.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun DriverProfileScreen(
    onBack: () -> Unit,
    onOpenVehicles: () -> Unit,
    onOpenDocuments: () -> Unit,
    onOpenRatings: () -> Unit,
    onOpenLevel: () -> Unit,
    onSignedOut: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: DriverProfileViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current

    LaunchedEffect(state.signedOut) {
        if (state.signedOut) onSignedOut()
    }
    LaunchedEffect(state.languageChanged) {
        // Resources are resolved once per configuration, so a language chosen here has no effect
        // until the Activity is rebuilt — the same move `LanguageCityScreen` makes on Continue.
        if (state.languageChanged) context.findActivity()?.recreate()
    }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.profile_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.action_back),
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
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }

            Column(
                modifier = Modifier.padding(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                IdentityCard(state = state, onEdit = { viewModel.openSheet(ProfileSheet.Name) })

                // Δ MCS-24 — the platform id, displaced from the header and kept.
                //
                // SCR-DA-023 asks another driver to type this to send credit, so it has to be
                // readable AND copyable — an id read aloud is a transfer nobody completes. The tap
                // copies rather than navigating, which is why it carries no chevron.
                state.profile?.userId?.let { userId ->
                    val clipboard = LocalClipboardManager.current

                    ProfileRow(
                        icon = Icons.Outlined.ContentCopy,
                        label = stringResource(R.string.profile_platform_id),
                        supporting = userId,
                        onClick = { clipboard.setText(AnnotatedString(userId)) },
                    )
                }

                ProfileRow(
                    icon = Icons.Outlined.DirectionsCar,
                    label = stringResource(R.string.profile_vehicle_details),
                    onClick = onOpenVehicles,
                )
                // Δ MCS-28 — where a driver goes to look at their own licence. Under the vehicle
                // row because the two lead to the same screen from different questions: "what am I
                // driving" and "show me the paperwork for it".
                ProfileRow(
                    icon = Icons.Outlined.Description,
                    label = stringResource(R.string.documents_open),
                    onClick = onOpenDocuments,
                )
                ProfileRow(
                    icon = Icons.Outlined.StarOutline,
                    label = stringResource(R.string.profile_trip_ratings),
                    onClick = onOpenRatings,
                )
                ProfileRow(
                    icon = Icons.Outlined.EmergencyShare,
                    label = stringResource(R.string.profile_emergency_row),
                    supporting = state.contact
                        ?.let { "${it.name} ${Symbols.DOT} ${it.phone}" }
                        ?: stringResource(R.string.profile_emergency_none),
                    trailing = { EditGlyph() },
                    onClick = { viewModel.openSheet(ProfileSheet.Emergency) },
                )
                ProfileRow(
                    icon = Icons.Outlined.WorkspacePremium,
                    label = stringResource(R.string.profile_driver_level),
                    supporting = state.standing.standing
                        ?.let { stringResource(R.string.profile_level_value, it.level) },
                    onClick = onOpenLevel,
                )
                ProfileRow(
                    icon = Icons.Outlined.Language,
                    label = stringResource(R.string.profile_language_row),
                    supporting = state.profile?.language?.endonym,
                    onClick = { viewModel.openSheet(ProfileSheet.Language) },
                )

                NotificationSwitches(state = state, onToggle = viewModel::setNotificationGroup)

                OutlinedButton(
                    onClick = viewModel::logOut,
                    modifier = Modifier.fillMaxWidth(),
                    enabled = !state.saving,
                ) {
                    Text(
                        text = stringResource(R.string.profile_log_out),
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        }
    }

    ProfileEditorSheet(state = state, viewModel = viewModel)
}

/**
 * The wireframe's avatar card — *"K. Fernando · DRV-22011 · ★4.8 overall"*.
 *
 * **Δ MCS-24 — the platform id is gone from here and the live vehicle is in its place.** The id is
 * real and this is the screen a driver reads it off before another driver types it into SCR-DA-023
 * — but it does not belong on the line that answers "who am I and what am I driving", and the
 * wireframe's own second line is the vehicle and the rating, not an identifier.
 *
 * The id is NOT dropped. C073's handoff names this screen as the one a driver reads their own
 * platform id off before another driver types it into SCR-DA-023, so deleting it would break a
 * credit transfer; it moves to a row of its own, with a copy action, which is what the iOS mirror
 * already does for the same reason.
 *
 * The layout is [DriverHeader], which SCR-DA-036's menu header also draws. Before this they were
 * two independent pieces of layout for one block, and they had already drifted in opposite
 * directions — the menu showing the app's name, this one showing an id.
 */
@Composable
private fun IdentityCard(state: DriverProfileState, onEdit: () -> Unit, modifier: Modifier = Modifier) {
    DriverHeader(
        state = DriverHeaderState(
            // Δ MCS-29 — the cache is the fallback, not the source: a read that has answered wins,
            // and until one does the driver sees the name this handset already knew.
            name = state.profile?.firstName ?: state.cachedName,
            level = state.standing.standing?.level ?: state.cachedLevel,
            registration = state.registration,
            // No app-facing read carries a driver's own star average — see DriverHeaderState.
            rating = null,
            photo = state.photo,
        ),
        modifier = modifier.padding(vertical = MageRideTheme.spacing.xs),
        trailing = { IconButton(onClick = onEdit) { EditGlyph() } },
    )
}

/** The wireframe's `listrow` — an icon, a label, an optional second line and a chevron. */
@Composable
private fun ProfileRow(
    icon: ImageVector,
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    supporting: String? = null,
    trailing: (@Composable () -> Unit)? = null,
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.surface,
        onClick = onClick,
    ) {
        Row(
            modifier = Modifier.padding(vertical = MageRideTheme.spacing.xs),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.RowIcon),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = label,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                supporting?.let {
                    Text(
                        text = it,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            trailing?.invoke() ?: Icon(
                imageVector = Icons.AutoMirrored.Outlined.KeyboardArrowRight,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.RowIcon),
                tint = MaterialTheme.colorScheme.outlineVariant,
            )
        }
    }
}

/** US-10.7's per-type switches, grouped — see [DriverNotificationGroup] for why five and not fifteen. */
@Composable
private fun NotificationSwitches(
    state: DriverProfileState,
    onToggle: (DriverNotificationGroup, Boolean) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(top = MageRideTheme.spacing.xs),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs),
    ) {
        SectionLabel(text = stringResource(R.string.profile_notify_heading))
        DriverNotificationGroup.entries.forEach { group ->
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = stringResource(group.label),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Switch(
                    checked = group.isEnabled(state.notificationPreferences),
                    onCheckedChange = { enabled -> onToggle(group, enabled) },
                    enabled = !state.saving && state.profile != null,
                )
            }
        }
        Text(
            text = stringResource(R.string.profile_notify_safety_note),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** The wireframe's `✎`. */
@Composable
private fun EditGlyph() {
    Icon(
        imageVector = Icons.Outlined.Edit,
        contentDescription = stringResource(R.string.action_edit),
        modifier = Modifier.size(ControlTokens.RowIcon),
        tint = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}
