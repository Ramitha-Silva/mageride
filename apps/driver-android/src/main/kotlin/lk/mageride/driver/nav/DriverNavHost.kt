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
import lk.mageride.driver.ui.theme.MageRideTheme

/**
 * The app's single `NavHost`, with one entry per [DriverRoute].
 *
 * **Every destination is registered here and every one is a placeholder today.** That is the
 * shape C067 is supposed to leave behind: the host exists, the graph is complete, and each screen
 * group replaces the body of its own routes without touching the graph. A component that added a
 * destination of its own would put the app's navigation in eight files.
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
        placeholder(DriverRoute.Splash, "SCR-DA-001 splash")
        placeholder(DriverRoute.LanguageCity, "SCR-DA-002 language / city")
        placeholder(DriverRoute.Login, "SCR-DA-003 phone + OTP")
        placeholder(DriverRoute.ProfileSetup, "SCR-DA-003a profile setup")
        placeholder(DriverRoute.Permissions, "SCR-DA-007 permissions")

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
