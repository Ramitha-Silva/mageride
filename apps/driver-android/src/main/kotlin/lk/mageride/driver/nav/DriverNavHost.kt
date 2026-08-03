package lk.mageride.driver.nav

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.navigation.NavGraphBuilder
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import lk.mageride.driver.R
import lk.mageride.driver.capture.DocumentScannerScreen
import lk.mageride.driver.earnings.EarningsScreen
import lk.mageride.driver.home.DirectionalScreen
import lk.mageride.driver.home.HomeScreen
import lk.mageride.driver.jobs.JobBoardScreen
import lk.mageride.driver.jobs.ScheduledRidesScreen
import lk.mageride.driver.level.DriverLevelScreen
import lk.mageride.driver.menu.MenuScreen
import lk.mageride.driver.onboarding.LanguageCityScreen
import lk.mageride.driver.onboarding.LoginScreen
import lk.mageride.driver.onboarding.PermissionsScreen
import lk.mageride.driver.onboarding.ProfileSetupScreen
import lk.mageride.driver.onboarding.SplashScreen
import lk.mageride.driver.ride.ActiveRideScreen
import lk.mageride.driver.ui.theme.MageRideTheme
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
 * C068–C073 have taken their routes; the rest are still standing placeholders.
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

        // ---- C074–C075 · profile, documents, support -----------------------------------
        placeholder(DriverRoute.Documents, "SCR-DA-029a documents")
        placeholder(DriverRoute.Profile, "SCR-DA-029 profile")
        placeholder(DriverRoute.Support, "SCR-DA-033 support")

        // The four destinations SCR-DA-036's drawer names and the shell's table was missing;
        // C070 added the routes so no drawer row is dead, and their groups replace these lines.
        placeholder(DriverRoute.TrackerPairing, "SCR-DA-027 tracker pairing")
        placeholder(DriverRoute.Sharing, "SCR-DA-028 sharing management")
        placeholder(DriverRoute.RideHistory, "SCR-DA-030 ride history")
        placeholder(DriverRoute.Notifications, "SCR-DA-034 notifications")
    }
}

/** Registers [route] with the standing placeholder. One line per screen the shell is waiting for. */
private fun NavGraphBuilder.placeholder(route: DriverRoute, screen: String) {
    composable(route.path) { RoutePlaceholder(screen) }
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

/**
 * What a registered-but-unbuilt destination shows.
 *
 * Naming the screen id is deliberate: during wave 4a someone will open a tab whose group has not
 * landed, and "SCR-DA-014 jobs arrives with its screen group" tells them which prompt owns it.
 * The copy itself is trilingual like everything else — see the three `strings.xml` files.
 */
@Composable
private fun RoutePlaceholder(screen: String) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(MageRideTheme.spacing.lg),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.route_placeholder_title),
            style = MaterialTheme.typography.titleLarge,
            color = MaterialTheme.colorScheme.onSurface,
        )
        Text(
            text = stringResource(R.string.route_placeholder_body, screen),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
