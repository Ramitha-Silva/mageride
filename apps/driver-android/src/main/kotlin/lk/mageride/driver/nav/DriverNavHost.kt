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
import lk.mageride.driver.onboarding.LanguageCityScreen
import lk.mageride.driver.onboarding.LoginScreen
import lk.mageride.driver.onboarding.PermissionsScreen
import lk.mageride.driver.onboarding.ProfileSetupScreen
import lk.mageride.driver.onboarding.SplashScreen
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * The app's single `NavHost`, with one entry per [DriverRoute].
 *
 * **Every destination is registered here, and a screen group replaces the body of its own routes
 * without touching the graph.** That is the shape C067 left behind and the shape it keeps: a
 * component that added a destination of its own would put the app's navigation in eight files.
 * C068 has taken the five cluster-1 routes; the rest are still standing placeholders.
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
        placeholder(DriverRoute.VehicleOnboarding, "SCR-DA-004 vehicle onboarding")
        placeholder(DriverRoute.DocumentCapture, "SCR-DA-005 document capture")
        placeholder(DriverRoute.VehicleOnboardingStatus, "SCR-DA-006 onboarding status")
        placeholder(DriverRoute.Vehicles, "SCR-DA-026 my vehicles")

        // ---- C070 · dashboard / dispatch -----------------------------------------------
        placeholder(DriverRoute.Home, "SCR-DA-010 dashboard")
        composable(DriverRoute.ActiveRide.PATTERN) {
            RoutePlaceholder("SCR-DA-011 active ride")
        }

        // ---- C071–C075 · jobs, wallet, menu, profile, documents, support ---------------
        placeholder(DriverRoute.Jobs, "SCR-DA-014 jobs")
        placeholder(DriverRoute.Wallet, "SCR-DA-020 wallet")
        placeholder(DriverRoute.Menu, "SCR-DA-030 menu")
        placeholder(DriverRoute.Documents, "SCR-DA-028 documents")
        placeholder(DriverRoute.Profile, "SCR-DA-027 profile")
        placeholder(DriverRoute.Support, "SCR-DA-031 support")
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
