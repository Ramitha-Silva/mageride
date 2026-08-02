package lk.mageride.driver.onboarding

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.BatteryFull
import androidx.compose.material.icons.outlined.Layers
import androidx.compose.material.icons.outlined.LocationOn
import androidx.compose.material.icons.outlined.Notifications
import androidx.compose.material.icons.outlined.Schedule
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import org.koin.compose.koinInject

/**
 * **SCR-DA-007 · permissions** — the last gate before the dashboard.
 *
 * The wireframe's four rows, each with its own switch: location (always/background),
 * notifications, battery-optimisation off, display over apps. Tapping a row that is not granted
 * asks for it; a runtime permission the driver has already refused twice cannot be asked again by
 * anyone, so the row falls through to this app's settings page — D2's *"denied → Settings
 * deep-link"*.
 *
 * **Continue is never disabled.** AL-27 puts nothing between Profile Setup and Home, and a screen
 * a driver cannot leave is one they uninstall. What a refusal costs them is going *online*, which
 * is the dashboard's gate (US-9.6) and says so there.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun PermissionsScreen(onContinue: () -> Unit, modifier: Modifier = Modifier) {
    val permissions = koinInject<DriverPermissions>()
    val preferences = koinInject<OnboardingPreferences>()
    val context = LocalContext.current

    var granted by remember { mutableStateOf(DriverPermission.entries.associateWith(permissions::isGranted)) }
    var requesting by remember { mutableStateOf<DriverPermission?>(null) }

    val refresh = { granted = DriverPermission.entries.associateWith(permissions::isGranted) }

    // A settings screen does not report back; the only signal that anything changed is coming
    // back to the foreground. Re-reading on RESUME is what makes the switches true after one.
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) refresh()
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    val requestPermissions = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { results ->
        val target = requesting
        requesting = null
        refresh()
        // Every result false means the system showed nothing — the driver has denied it
        // permanently, and only Settings can change it now.
        if (target != null && results.isNotEmpty() && results.values.none { it }) {
            context.startActivity(permissions.settingsIntent(target))
        }
    }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = { TopAppBar(title = { Text(text = stringResource(R.string.permissions_title)) }) },
    ) { insets ->
        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = stringResource(R.string.permissions_intro),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            DriverPermission.entries.forEach { permission ->
                PermissionRow(
                    permission = permission,
                    granted = granted[permission] == true,
                    onAsk = {
                        val requests = permissions.runtimeRequestsFor(permission)
                        if (permission.kind == PermissionKind.RUNTIME && requests.isNotEmpty()) {
                            requesting = permission
                            requestPermissions.launch(requests)
                        } else {
                            context.startActivity(permissions.settingsIntent(permission))
                        }
                    },
                )
            }

            MageRideCta(
                label = stringResource(R.string.permissions_continue),
                onClick = {
                    preferences.permissionsAcknowledged = true
                    onContinue()
                },
                modifier = Modifier.padding(top = MageRideTheme.spacing.md),
            )
        }
    }
}

/** One wireframe `listrow`: icon, title, rationale and the switch that reflects the OS's answer. */
@Composable
private fun PermissionRow(
    permission: DriverPermission,
    granted: Boolean,
    onAsk: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
    ) {
        Icon(
            imageVector = permission.icon(),
            contentDescription = null,
            modifier = Modifier.size(ControlTokens.RowIcon),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = stringResource(permission.title),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = stringResource(permission.rationale),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Box {
            // A granted permission cannot be revoked from inside the app, so the switch only ever
            // travels one way here; the OS's own settings are where it comes back off.
            Switch(checked = granted, onCheckedChange = { if (!granted) onAsk() })
        }
    }
}

/** The wireframe's row glyphs: 📍 🔔 🔋 ▢. */
private fun DriverPermission.icon(): ImageVector = when (this) {
    DriverPermission.LOCATION -> Icons.Outlined.LocationOn
    DriverPermission.BACKGROUND_LOCATION -> Icons.Outlined.Schedule
    DriverPermission.NOTIFICATIONS -> Icons.Outlined.Notifications
    DriverPermission.BATTERY -> Icons.Outlined.BatteryFull
    DriverPermission.OVERLAY -> Icons.Outlined.Layers
}
