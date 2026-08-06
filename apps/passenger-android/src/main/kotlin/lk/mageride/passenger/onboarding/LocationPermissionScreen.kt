package lk.mageride.passenger.onboarding

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.LocationOn
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import lk.mageride.passenger.R
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.passenger.ui.component.IllustrationPanel
import lk.mageride.passenger.ui.component.MageRideCta
import lk.mageride.passenger.ui.component.MageRideTextLink
import lk.mageride.passenger.ui.theme.MageRideTheme
import org.koin.compose.koinInject

/**
 * SCR-PA-005 — the location rationale.
 *
 * The wireframe: an illustration, a heading, one sentence of *why*, then *"Allow location"* over a
 * *"Not now"* link. The OS dialog it opens is Android's, drawn over this screen.
 *
 * **Both outcomes continue.** The rationale is a courtesy, not a gate: a passenger who denies still
 * reaches the map, which asks again when it actually needs a fix and shows the Colombo Fort
 * default until then. Trapping them here would be a screen with no way out, since Android stops
 * showing the system dialog after two refusals.
 *
 * **After those two refusals the CTA becomes "Open Settings".** `shouldShowRequestPermissionRationale`
 * is the usual way to detect that, and it needs an Activity; the simpler signal used here is that
 * the launcher came back denied at least once while the permission is still not granted — from
 * that point the only place a grant can happen is Settings, and a CTA that silently did nothing
 * would be worse than none.
 *
 * @param onContinue Move to SCR-PA-010, granted or not.
 */
@Composable
internal fun LocationPermissionScreen(
    onContinue: () -> Unit,
    permission: LocationPermission = LocationPermission(LocalContext.current),
    preferences: AppPreferences = koinInject(),
) {
    val context = LocalContext.current
    var refused by remember { mutableStateOf(false) }

    val acknowledgeAndContinue = {
        // What is remembered is that the screen was SHOWN, never the grant — the grant is the OS's
        // and can be revoked from Settings at any time, so a cached "yes" would be a lie the map
        // would then have to discover for itself.
        preferences.locationRationaleAcknowledged = true
        onContinue()
    }

    val launcher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestMultiplePermissions(),
    ) { grants ->
        // COARSE counts as granted — Android 12+ lets the user pick approximate, and a ~3 km live
        // map works at that precision. See `LocationPermission.isGranted`.
        if (grants.values.any { it }) acknowledgeAndContinue() else refused = true
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.md),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.md),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(modifier = Modifier.weight(1f))

        IllustrationPanel(
            icon = Icons.Outlined.LocationOn,
            caption = stringResource(R.string.permission_location_caption),
        )
        Text(
            text = stringResource(R.string.permission_location_title),
            style = MaterialTheme.typography.titleLarge,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center,
        )
        Text(
            text = stringResource(R.string.permission_location_body),
            modifier = Modifier.fillMaxWidth(),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
        if (refused) {
            Text(
                text = stringResource(R.string.permission_location_denied),
                modifier = Modifier.fillMaxWidth(),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.error,
                textAlign = TextAlign.Center,
            )
        }

        Box(modifier = Modifier.weight(1f))

        MageRideCta(
            label = if (refused) {
                stringResource(R.string.permission_open_settings)
            } else {
                stringResource(R.string.permission_allow_location)
            },
            onClick = {
                when {
                    permission.isGranted() -> acknowledgeAndContinue()
                    refused -> context.startActivity(permission.settingsIntent())
                    else -> launcher.launch(LocationPermission.REQUESTED)
                }
            },
        )
        MageRideTextLink(label = stringResource(R.string.permission_not_now), onClick = acknowledgeAndContinue)
    }
}
