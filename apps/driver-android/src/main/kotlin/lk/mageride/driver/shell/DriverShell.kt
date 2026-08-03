package lk.mageride.driver.shell

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Scaffold
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import lk.mageride.driver.nav.DriverBottomBar
import lk.mageride.driver.nav.DriverNavHost
import lk.mageride.driver.nav.DriverRoute
import lk.mageride.driver.nav.isTabRoute
import lk.mageride.driver.push.PushRouter
import lk.mageride.shared.data.api.MageRideApiSignals
import lk.mageride.shared.data.api.UpgradeRequiredSignal
import lk.mageride.shared.data.api.version.VersionApi
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionEvent
import org.koin.compose.koinInject

/**
 * The application shell: one Scaffold, one NavHost, and the three things that sit above every
 * screen regardless of which group owns it.
 *
 * 1. **The bottom navigation** (AL-31), on the four tab routes and nowhere else.
 * 2. **The offline banner** (US-15.6), which preserves the screen underneath.
 * 3. **The app-update gate** (D-31), which is the only thing here allowed to block.
 *
 * It also owns the two cross-cutting navigations no screen can be responsible for: a push that
 * names a destination, and a session that ended. Both are subscribed exactly once, here — a
 * screen that also handled `RouteToLogin` would race this one and pop the back stack twice.
 *
 * @param onOpenUrl Hands a store URL to the platform browser. Injected rather than called
 *   directly so the shell stays free of `Intent`, which is what lets it be previewed.
 */
@Composable
internal fun DriverShell(
    onOpenUrl: (String?) -> Unit,
    modifier: Modifier = Modifier,
    controller: NavHostController = rememberNavController(),
) {
    val connectivity = koinInject<ConnectivityMonitor>()
    val signals = koinInject<MageRideApiSignals>()
    val pushes = koinInject<PushRouter>()
    val sessions = koinInject<AuthSessionManager>()
    val versions = koinInject<VersionApi>()

    val online by connectivity.isOnline.collectAsStateWithLifecycle(initialValue = true)
    val backStackEntry by controller.currentBackStackEntryAsState()
    val currentPath = backStackEntry?.destination?.route

    var upgrade by rememberSaveable(
        stateSaver = UpgradeSignalSaver,
    ) { mutableStateOf<UpgradeRequiredSignal?>(null) }

    // D-31. `upgradeRequired` replays its last value, so subscribing after the failing call still
    // sees it — which is the case on a cold start whose very first request was refused.
    LaunchedEffect(signals) {
        signals.upgradeRequired.collect { upgrade = it }
    }

    // US-17.1/17.2 (Δ C075) — ask before anything else does. The gate is enforced at the edge on
    // every route, so without this the first thing a driver below the floor sees is a login screen
    // whose OTP request failed. `GET /v1/version/check` is public and unattested precisely so a
    // build too old to authenticate can still learn that it is too old, and it publishes on the
    // SAME signal a mid-session 426 does — one wall, one subscriber, either way in.
    LaunchedEffect(versions) {
        runCatching { versions.checkAppVersion() }
    }

    // A push that names a screen. `consume()` clears the replay cache so a rotation does not
    // navigate a second time.
    LaunchedEffect(pushes) {
        pushes.pending.collect { route ->
            controller.navigate(route.path) { launchSingleTop = true }
            pushes.consume()
        }
    }

    // C014 raises `RouteToLogin` for every way a session can end — logout, refresh failure,
    // `403 device-revoked` (AL-08), PDPA erasure. The shell clears the whole back stack, because
    // what is on it belongs to a user who is no longer signed in.
    LaunchedEffect(sessions) {
        sessions.events.collect { event ->
            if (event is SessionEvent.RouteToLogin) {
                controller.navigate(DriverRoute.Login.path) {
                    popUpTo(controller.graph.id) { inclusive = true }
                    launchSingleTop = true
                }
            }
        }
    }

    Scaffold(
        modifier = modifier.fillMaxSize(),
        bottomBar = {
            if (isTabRoute(currentPath)) {
                DriverBottomBar(current = currentPath) { route ->
                    controller.navigate(route.path) {
                        // Tab switching must not grow the back stack: four taps should leave one
                        // entry, and Back from a tab should leave the app, not walk the tabs.
                        popUpTo(DriverRoute.Home.path) { saveState = true }
                        launchSingleTop = true
                        restoreState = true
                    }
                }
            }
        },
    ) { insets ->
        Column(modifier = Modifier.padding(insets)) {
            OfflineBanner(visible = !online)
            DriverNavHost(controller = controller, modifier = Modifier.fillMaxSize())
        }
    }

    UpdateGate(
        signal = upgrade,
        onUpdate = onOpenUrl,
        onDismiss = { upgrade = null },
    )
}

/**
 * Keeps the 426 payload across a configuration change.
 *
 * `UpgradeRequiredSignal` is a plain C013 data class rather than a `Parcelable` — `:shared` cannot
 * depend on Android — so the three fields are saved as a list. Without this a rotation dismisses
 * a *mandatory* gate, and the next call would have to fail again to bring it back.
 */
private val UpgradeSignalSaver = androidx.compose.runtime.saveable.Saver<UpgradeRequiredSignal?, List<Any?>>(
    save = { signal -> signal?.let { listOf(it.latestVersion, it.updateUrl, it.isMandatory) } },
    restore = { saved ->
        UpgradeRequiredSignal(
            latestVersion = saved.getOrNull(0) as? String,
            updateUrl = saved.getOrNull(1) as? String,
            isMandatory = saved.getOrNull(2) as? Boolean == true,
        )
    },
)
