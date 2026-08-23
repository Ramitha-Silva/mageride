package lk.mageride.driver.nav

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import lk.mageride.driver.capture.DocumentScannerScreen
import lk.mageride.driver.comms.VoipCallScreen
import lk.mageride.driver.documents.DocumentsScreen
import lk.mageride.driver.earnings.EarningsScreen
import lk.mageride.driver.history.RideHistoryScreen
import lk.mageride.driver.home.DirectionalScreen
import lk.mageride.driver.home.HomeScreen
import lk.mageride.driver.jobs.JobBoardScreen
import lk.mageride.driver.jobs.ScheduledRidesScreen
import lk.mageride.driver.level.DriverLevelScreen
import lk.mageride.driver.menu.MenuScreen
import lk.mageride.driver.notifications.NotificationsScreen
import lk.mageride.driver.onboarding.LanguageCityScreen
import lk.mageride.driver.onboarding.LoginScreen
import lk.mageride.driver.onboarding.PermissionsScreen
import lk.mageride.driver.onboarding.ProfileSetupScreen
import lk.mageride.driver.onboarding.SplashScreen
import lk.mageride.driver.profile.DriverProfileScreen
import lk.mageride.driver.ride.ActiveRideScreen
import lk.mageride.driver.safety.SosScreen
import lk.mageride.driver.sharing.SharingScreen
import lk.mageride.driver.support.SupportScreen
import lk.mageride.driver.tracker.TrackerPairingScreen
import lk.mageride.driver.vehicle.VehicleOnboardingScreen
import lk.mageride.driver.vehicle.VehicleOnboardingStatusScreen
import lk.mageride.driver.vehicle.VehiclesScreen
import lk.mageride.driver.wallet.CreditTransferScreen
import lk.mageride.driver.wallet.RequestCreditScreen
import lk.mageride.driver.wallet.TopUpScreen
import lk.mageride.driver.wallet.WalletHistoryScreen
import lk.mageride.driver.wallet.WalletScreen

/**
 * The app's single `NavHost`, with one entry per [DriverRoute].
 *
 * **Every destination is registered here, and a screen group replaces the body of its own routes
 * without touching the graph.** That is the shape C067 left behind and the shape it keeps: a
 * component that added a destination of its own would put the app's navigation in eight files.
 * **Δ MCS-28 — every route now has a screen.** `Documents` was the last standing placeholder, and
 * the placeholder machinery went with it: a helper that registers "not built yet" is worth having
 * while something is not built, and is a way to lose a route quietly once nothing is.
 *
 * The start destination is [DriverRoute.Splash] — SCR-DA-001 is the driver-info router, and its
 * states ("no token → Login · registered/not approved → RegistrationHub · approved+perms →
 * Dashboard") are exactly why the first screen cannot be a tab.
 */
@Composable
internal fun DriverNavHost(controller: NavHostController, modifier: Modifier = Modifier) {
    NavHost(
        navController = controller,
        startDestination = DriverRoute.Splash.path,
        modifier = modifier,
    ) {
        // ---- C068 · auth / onboarding --------------------------------------------------
        composable(DriverRoute.Splash.path) {
            SplashScreen(onResolved = { controller.replaceOnboarding(it.route) })
        }
        composable(DriverRoute.LanguageCity.path) {
            LanguageCityScreen(onContinue = { controller.replaceOnboarding(DriverRoute.Login) })
        }
        composable(DriverRoute.Login.path) {
            LoginScreen(
                onSignedIn = { controller.replaceOnboarding(it.route) },
                onBack = { controller.popBackStack() },
            )
        }
        composable(DriverRoute.ProfileSetup.path) {
            ProfileSetupScreen(
                // AL-43: the scanner is SCR-DA-005's and C069 owns it. Profile Setup stays on the
                // back stack so the captured image returns to the form it belongs to.
                onCaptureRequested = { controller.navigate(DriverRoute.DocumentCapture.path) },
                onComplete = { controller.replaceOnboarding(DriverRoute.Permissions) },
            )
        }
        composable(DriverRoute.Permissions.path) {
            PermissionsScreen(onContinue = { controller.replaceOnboarding(DriverRoute.Home) })
        }

        // ---- C069 · vehicle onboarding -------------------------------------------------
        composable(DriverRoute.VehicleOnboarding.path) {
            VehicleOnboardingScreen(
                onCaptureRequested = { controller.navigate(DriverRoute.DocumentCapture.path) },
                // Step 4/4 is saved, so the wizard is done with. Replacing it rather than stacking
                // is what stops Back from SCR-DA-006 re-opening a step the driver has submitted.
                onSubmitted = {
                    controller.navigate(DriverRoute.VehicleOnboardingStatus.path) {
                        popUpTo(DriverRoute.VehicleOnboarding.path) { inclusive = true }
                    }
                },
                // "Back exits the wizard" from Step 1/4 (D2' §SCR-DA-004). Every other step's
                // back is a step backwards and never reaches here.
                onExit = { controller.popBackStack() },
            )
        }
        composable(DriverRoute.DocumentCapture.path) {
            // SCR-DA-005 delivers through `DocumentCaptureCoordinator`; the screen that asked for
            // the capture is still on the back stack underneath and collects it there.
            DocumentScannerScreen(onFinished = { controller.popBackStack() })
        }
        composable(DriverRoute.VehicleOnboardingStatus.path) {
            VehicleOnboardingStatusScreen(
                onDone = {
                    controller.navigate(DriverRoute.Vehicles.path) {
                        popUpTo(DriverRoute.VehicleOnboardingStatus.path) { inclusive = true }
                    }
                },
                onResume = {
                    controller.navigate(DriverRoute.VehicleOnboarding.path) {
                        popUpTo(DriverRoute.VehicleOnboardingStatus.path) { inclusive = true }
                    }
                },
            )
        }
        composable(DriverRoute.Vehicles.path) {
            VehiclesScreen(
                // The ＋ and a row's Resume are the same navigation: AL-30 decides whether the
                // wizard opens at the next incomplete step or starts a NEW vehicle at Step 1/4,
                // and it decides that in the repository, not here.
                onAddVehicle = { controller.navigate(DriverRoute.VehicleOnboarding.path) },
                onOpenStatus = { controller.navigate(DriverRoute.VehicleOnboardingStatus.path) },
                onOpenDocuments = { controller.navigate(DriverRoute.Documents.path) },
                onBack = { controller.popBackStack() },
            )
        }

        // ---- C070 · dashboard / dispatch / active ride ---------------------------------
        composable(DriverRoute.Home.path) {
            HomeScreen(
                onOpenDirectional = { controller.navigate(DriverRoute.Directional.path) },
                // D2' §SCR-DA-019's traceability row is "Driver Level badge | SCR-DA-019 +
                // SCR-DA-010 badge", so the `L3` in the dashboard's app bar is what opens the level
                // screen, and the "Today: 4 trips · Rs 3,180" line is what opens SCR-DA-020. Both
                // are pushed — the wireframes draw them with a `‹` app bar (Δ C072).
                onOpenLevel = { controller.navigate(DriverRoute.DriverLevel.path) },
                onOpenEarnings = { controller.navigate(DriverRoute.Earnings.path) },
                // US-9.6's empty state. My Vehicles raises SCR-DA-026a for itself when the list
                // comes back empty, so there is one screen for "no vehicle" rather than two.
                onOpenVehicles = { controller.navigate(DriverRoute.Vehicles.path) },
                // Winning an offer replaces Home rather than stacking on it: a driver on a ride has
                // no standby map to go Back to, and SCR-DA-015 is where the app should be restored
                // to if it is killed mid-trip.
                onOpenRide = { rideId ->
                    controller.navigate(DriverRoute.ActiveRide(rideId).path) { launchSingleTop = true }
                },
            )
        }
        composable(DriverRoute.Directional.path) {
            DirectionalScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.ActiveRide.PATTERN) { entry ->
            ActiveRideScreen(
                rideId = entry.arguments?.getString(DriverRoute.ActiveRide.ARG_RIDE_ID).orEmpty(),
                // The ride reached a terminal state. Back to Home, and nothing of the ride left on
                // the stack — a completed trip must not be reachable with a swipe.
                onFinished = {
                    controller.navigate(DriverRoute.Home.path) {
                        popUpTo(DriverRoute.Home.path) { inclusive = true }
                        launchSingleTop = true
                    }
                },
                // Both are pushed over the ride and pop back to it (Δ C075): a call and an alarm
                // are things that happen *during* a trip, and the trip is what the driver returns
                // to. `launchSingleTop` because a second tap on either must not stack a second one.
                onOpenVoipCall = { rideId ->
                    controller.navigate(DriverRoute.VoipCall(rideId).path) { launchSingleTop = true }
                },
                onOpenSos = { rideId ->
                    controller.navigate(DriverRoute.Sos(rideId).path) { launchSingleTop = true }
                },
            )
        }

        // ---- C070 · the Menu tab (AL-31's drawer) --------------------------------------
        composable(DriverRoute.Menu.path) {
            MenuScreen(
                onOpen = { route -> controller.navigate(route.path) },
                onClose = { controller.popBackStack() },
            )
        }

        // ---- C072 · jobs, level, earnings ----------------------------------------------
        composable(DriverRoute.Jobs.path) {
            JobBoardScreen(onOpenScheduled = { controller.navigate(DriverRoute.ScheduledRides.path) })
        }
        composable(DriverRoute.ScheduledRides.path) {
            ScheduledRidesScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.DriverLevel.path) {
            DriverLevelScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.Earnings.path) {
            EarningsScreen(onBack = { controller.popBackStack() })
        }

        // ---- C073 · wallet, daily fee, credit transfer ---------------------------------
        composable(DriverRoute.Wallet.path) {
            WalletScreen(
                // SCR-DA-021's two button rows are the only entry points to the other four; each
                // is pushed, because each wireframe draws a `‹` app bar over a full screen.
                onTopUp = { controller.navigate(DriverRoute.WalletTopUp.path) },
                onRequestCredit = { controller.navigate(DriverRoute.WalletRequestCredit.path) },
                onTransferCredit = { controller.navigate(DriverRoute.WalletTransfer.path) },
                onHistory = { controller.navigate(DriverRoute.WalletHistory.path) },
            )
        }
        composable(DriverRoute.WalletTopUp.path) {
            TopUpScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.WalletRequestCredit.path) {
            RequestCreditScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.WalletTransfer.path) {
            CreditTransferScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.WalletHistory.path) {
            WalletHistoryScreen(onBack = { controller.popBackStack() })
        }

        // ---- C074 · tracker, sharing, profile, history ----------------------------------
        composable(DriverRoute.TrackerPairing.path) {
            TrackerPairingScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.Sharing.path) {
            SharingScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.RideHistory.path) {
            RideHistoryScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.Profile.path) {
            DriverProfileScreen(
                onBack = { controller.popBackStack() },
                // The wireframe's four rows. "Per-trip ratings" is SCR-DA-030 rather than a screen
                // of its own — US-18.3's per-trip stars live on the trips, and there is no read
                // that returns ratings apart from them.
                onOpenVehicles = { controller.navigate(DriverRoute.Vehicles.path) },
                onOpenDocuments = { controller.navigate(DriverRoute.Documents.path) },
                onOpenRatings = { controller.navigate(DriverRoute.RideHistory.path) },
                onOpenLevel = { controller.navigate(DriverRoute.DriverLevel.path) },
                // Log out is a one-way door out of the whole graph, exactly like the onboarding
                // steps that lead in: the splash re-routes on the session it can no longer find,
                // and nothing signed-in is left on the back stack to swipe back to.
                onSignedOut = { controller.replaceOnboarding(DriverRoute.Splash) },
            )
        }

        // ---- C075 · comms, safety, support, alerts --------------------------------------
        composable(DriverRoute.VoipCall.PATTERN) { entry ->
            VoipCallScreen(
                rideId = entry.arguments?.getString(DriverRoute.VoipCall.ARG_RIDE_ID).orEmpty(),
                // Hung up, dialled normally, or dismissed. Back to the ride underneath — never to
                // Home: the trip did not end because a call did.
                onFinished = { controller.popBackStack() },
            )
        }
        composable(DriverRoute.Sos.PATTERN) { entry ->
            SosScreen(
                rideId = entry.arguments?.getString(DriverRoute.Sos.ARG_RIDE_ID).orEmpty(),
                onFinished = { controller.popBackStack() },
            )
        }
        composable(DriverRoute.Support.path) {
            SupportScreen(onBack = { controller.popBackStack() })
        }
        composable(DriverRoute.Notifications.path) {
            NotificationsScreen(
                onBack = { controller.popBackStack() },
                // An alert's deep link is already resolved to a known destination — an
                // unrecognised one never reaches here. See `NotificationsViewModel`.
                onOpen = { route -> controller.navigate(route.path) { launchSingleTop = true } },
            )
        }

        // SCR-DA-029a — the driver's own documents (E-03, `mageride://documents`).
        //
        // Δ MCS-28 — the last standing placeholder, and it stood because there was nothing to draw:
        // `VehicleDocument` carried a kind, a status and an expiry and NO image, so a screen here
        // could have told a driver their insurance expired in March and not shown it to them. The
        // signed image links exist now, so it does.
        composable(DriverRoute.Documents.path) {
            DocumentsScreen(onBack = { controller.popBackStack() })
        }
    }
}

/**
 * Moves forward through onboarding, leaving nothing behind.
 *
 * Every step of cluster 1 is a one-way door: Back from Login must leave the app rather than
 * return to the splash, Back from Profile Setup must not return to the OTP screen of a session
 * that already exists, and Back from Home must never re-enter onboarding at all. Popping the
 * whole graph on each step is what makes all three true with one rule (C068).
 */
private fun NavHostController.replaceOnboarding(route: DriverRoute) {
    navigate(route.path) {
        popUpTo(graph.id) { inclusive = true }
        launchSingleTop = true
    }
}
