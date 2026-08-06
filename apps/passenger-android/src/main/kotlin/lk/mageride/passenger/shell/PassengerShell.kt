package lk.mageride.passenger.shell

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.Saver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import kotlinx.coroutines.launch
import lk.mageride.passenger.R
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.passenger.nav.PassengerBottomBar
import lk.mageride.passenger.nav.PassengerDrawerSheet
import lk.mageride.passenger.nav.PassengerNavHost
import lk.mageride.passenger.nav.PassengerRoute
import lk.mageride.passenger.nav.isTabRoute
import lk.mageride.passenger.push.PushRouter
import lk.mageride.passenger.settings.PassengerDrawerHeader
import lk.mageride.passenger.settings.PassengerIdentity
import lk.mageride.shared.data.api.MageRideApiSignals
import lk.mageride.shared.data.api.UpgradeRequiredSignal
import lk.mageride.shared.data.api.version.VersionApi
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionEvent
import org.koin.compose.koinInject

/**
 * How a screen opens SCR-PA-033's drawer.
 *
 * The wireframe draws a `≡` in the app bar of every cluster-2 screen, and that button belongs to
 * the screen while the drawer belongs to the shell. A composition local rather than a parameter
 * threaded through eight screen groups: the drawer is chrome, and a screen that had to be *given*
 * the ability to open it would be a screen that could be composed without one.
 *
 * The default throws nothing and does nothing — a `@Preview` of a screen has no shell around it,
 * and a preview that crashed on a composition local would be worse than one whose `≡` is inert.
 */
internal val LocalDrawerControl = staticCompositionLocalOf<() -> Unit> { {} }

/**
 * The application shell: one drawer, one Scaffold, one NavHost, and the things that sit above
 * every screen regardless of which group owns it.
 *
 * 1. **SCR-PA-033's modal navigation drawer**, opened from the `≡` in a screen's app bar
 *    ([LocalDrawerControl]) or from the Menu tab. Both doors are here.
 * 2. **The bottom navigation**, on the three tab routes and nowhere else.
 * 3. **SCR-PA-032's offline banner** (US-15.6), which preserves the screen underneath.
 * 4. **SCR-PA-031's update gate** (D-31) — a wall when the `426` is mandatory, a dismissible
 *    snackbar when it is not.
 *
 * It also owns the three cross-cutting behaviours no screen can be responsible for: a push that
 * names a destination, a session that ended, and the live socket. Each is subscribed exactly once,
 * here — a screen that also handled `RouteToLogin` would race this one and pop the back stack
 * twice.
 *
 * @param onOpenUrl Hands a store URL to the platform browser. Injected rather than called directly
 *   so the shell stays free of `Intent`, which is what lets it be previewed.
 */
@Composable
@Suppress("LongMethod") // The shell is a layout tree plus one subscription per cross-cutting rule.
internal fun PassengerShell(
    onOpenUrl: (String?) -> Unit,
    modifier: Modifier = Modifier,
    controller: NavHostController = rememberNavController(),
) {
    val connectivity = koinInject<ConnectivityMonitor>()
    val signals = koinInject<MageRideApiSignals>()
    val pushes = koinInject<PushRouter>()
    val sessions = koinInject<AuthSessionManager>()
    val versions = koinInject<VersionApi>()
    val live = koinInject<PassengerLiveMap>()
    val identity = koinInject<PassengerIdentity>()

    val online by connectivity.isOnline.collectAsStateWithLifecycle(initialValue = true)
    val backStackEntry by controller.currentBackStackEntryAsState()
    val currentPath = backStackEntry?.destination?.route

    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val snackbars = remember { SnackbarHostState() }
    val uiScope = rememberCoroutineScope()

    // Resolved here rather than inside the effect that shows them: `stringResource` is composable
    // and a `LaunchedEffect` body is not.
    val softUpdateMessage = stringResource(R.string.update_optional_message)
    val softUpdateAction = stringResource(R.string.update_action_now)

    var upgrade by rememberSaveable(stateSaver = UpgradeSignalSaver) {
        mutableStateOf<UpgradeRequiredSignal?>(null)
    }

    // D-31. `upgradeRequired` replays its last value, so subscribing after the failing call still
    // sees it — which is the case on a cold start whose very first request was refused.
    LaunchedEffect(signals) {
        signals.upgradeRequired.collect { upgrade = it }
    }

    // US-17.1/17.2 — ask before anything else does. The gate is enforced at the edge on every
    // route, so without this the first thing a passenger below the floor sees is a login screen
    // whose OTP request failed. `GET /v1/version/check` is public and unattested precisely so a
    // build too old to authenticate can still learn that it is too old, and it publishes on the
    // SAME signal a mid-session 426 does — one wall, one subscriber, either way in.
    LaunchedEffect(versions) {
        runCatching { versions.checkAppVersion() }
    }

    // SCR-PA-031's soft half: "soft = dismissible banner/snackbar". A dialog would be a wall, and
    // the whole point of a soft signal is that the release did not require one. Clearing `upgrade`
    // afterwards is what stops it reappearing on every recomposition; a build that is genuinely
    // still outdated hears about it again on the next 426.
    LaunchedEffect(upgrade) {
        val signal = upgrade
        if (signal == null || signal.isMandatory) return@LaunchedEffect
        val result = snackbars.showSnackbar(
            message = softUpdateMessage,
            actionLabel = softUpdateAction,
            withDismissAction = true,
        )
        if (result == SnackbarResult.ActionPerformed) onOpenUrl(signal.updateUrl)
        upgrade = null
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
    // what is on it belongs to a user who is no longer signed in, and drops the live plane with
    // it: the socket's credential was that session's access token (D-29).
    LaunchedEffect(sessions) {
        sessions.events.collect { event ->
            if (event is SessionEvent.RouteToLogin) {
                live.disconnect()
                // The drawer header belongs to the session that just ended (Δ C083); leaving it
                // would show the previous passenger's name behind the next one's login.
                identity.clear()
                drawerState.close()
                controller.navigate(PassengerRoute.Login.path) {
                    popUpTo(controller.graph.id) { inclusive = true }
                    launchSingleTop = true
                }
            }
        }
    }

    // The socket is process-scoped, not composition-scoped — see `liveMapBindings`. `connect()` is
    // idempotent, so calling it from here is "make sure it is up" rather than "open one now", and
    // the only things that take it down are a signed-out session and the process ending. Nothing
    // is *joined* until a position arrives; see `PassengerLiveMap.onPosition`.
    LaunchedEffect(live) {
        live.connect()
    }

    ModalNavigationDrawer(
        drawerState = drawerState,
        // Swipe-to-open is off everywhere but the three tabs: SCR-PA-017's QR camera and
        // SCR-PA-029's alarm are full-screen takeovers, and a drawer that could be dragged out
        // from the left edge of either is a drawer in front of something urgent.
        gesturesEnabled = drawerState.isOpen || isTabRoute(currentPath),
        drawerContent = {
            PassengerDrawerSheet(
                onOpen = { route ->
                    uiScope.launch { drawerState.close() }
                    controller.navigate(route.path) { launchSingleTop = true }
                },
                // Log out ends the session and nothing else. The `RouteToLogin` collector above is
                // what clears the back stack — one subscriber for every way a session can end, so
                // a logout and a revocation take exactly the same path out of the app.
                onLogOut = {
                    identity.clear()
                    uiScope.launch {
                        drawerState.close()
                        runCatching { sessions.logout() }
                    }
                },
                // SCR-PA-033's identity block (Δ C083). The shell owns the drawer and its rows; the
                // name, id and phone in the header come from `GET /v1/users/me`, which is a screen's
                // data layer — so it arrives as the slot `PassengerDrawerSheet` left for it.
                header = { PassengerDrawerHeader() },
            )
        },
    ) {
        CompositionLocalProvider(LocalDrawerControl provides { uiScope.launch { drawerState.open() } }) {
            Scaffold(
                modifier = modifier.fillMaxSize(),
                snackbarHost = { SnackbarHost(snackbars) },
                bottomBar = {
                    if (isTabRoute(currentPath)) {
                        PassengerBottomBar(
                            current = currentPath,
                            drawerOpen = drawerState.isOpen,
                            onSelect = { route ->
                                controller.navigate(route.path) {
                                    // Tab switching must not grow the back stack: three taps
                                    // should leave one entry, and Back from a tab should leave the
                                    // app rather than walk the tabs.
                                    popUpTo(PassengerRoute.LiveMap.path) { saveState = true }
                                    launchSingleTop = true
                                    restoreState = true
                                }
                            },
                            onOpenMenu = { uiScope.launch { drawerState.open() } },
                        )
                    }
                },
            ) { insets ->
                Column(modifier = Modifier.padding(insets)) {
                    OfflineBanner(visible = !online)
                    PassengerNavHost(controller = controller, modifier = Modifier.fillMaxSize())
                }
            }
        }
    }

    UpdateGate(signal = upgrade, onUpdate = onOpenUrl)
}

/**
 * Keeps the 426 payload across a configuration change.
 *
 * `UpgradeRequiredSignal` is a plain C013 data class rather than a `Parcelable` — `:shared` cannot
 * depend on Android — so the three fields are saved as a list. Without this a rotation dismisses a
 * *mandatory* gate, and the next call would have to fail again to bring it back.
 */
private val UpgradeSignalSaver = Saver<UpgradeRequiredSignal?, List<Any?>>(
    save = { signal -> signal?.let { listOf(it.latestVersion, it.updateUrl, it.isMandatory) } },
    restore = { saved ->
        UpgradeRequiredSignal(
            latestVersion = saved.getOrNull(0) as? String,
            updateUrl = saved.getOrNull(1) as? String,
            isMandatory = saved.getOrNull(2) as? Boolean == true,
        )
    },
)
