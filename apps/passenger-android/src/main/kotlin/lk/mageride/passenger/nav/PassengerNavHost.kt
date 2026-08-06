package lk.mageride.passenger.nav

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
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.navArgument
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.MageRideTheme

/**
 * The app's single `NavHost`, with one entry per [PassengerRoute].
 *
 * **Every destination is registered here, and a screen group replaces the body of its own routes
 * without touching the graph.** A component that added a destination of its own would put the
 * app's navigation in eight files. C077–C084 take their routes one cluster at a time; until then
 * every one of them is the standing placeholder below, which names the screen id so whoever opens
 * the tab knows which prompt owns it.
 *
 * The start destination is [PassengerRoute.Splash] — SCR-PA-001 is the session router, and its
 * states (*"KMP auth validates token → onboarding / login / live_map, resumes an active ride via
 * `GET /v1/rides/passenger/{id}/active`"*) are exactly why the first screen cannot be a tab.
 */
@Composable
internal fun PassengerNavHost(controller: NavHostController, modifier: Modifier = Modifier) {
    NavHost(
        navController = controller,
        startDestination = PassengerRoute.Splash.path,
        modifier = modifier,
    ) {
        // ---- C077 · auth / onboarding --------------------------------------------------
        placeholder(PassengerRoute.Splash, "SCR-PA-001 splash")
        placeholder(PassengerRoute.Onboarding, "SCR-PA-002 onboarding + language")
        placeholder(PassengerRoute.Login, "SCR-PA-003 phone + OTP")
        placeholder(PassengerRoute.ProfileSetup, "SCR-PA-004 profile setup")
        placeholder(PassengerRoute.LocationPermission, "SCR-PA-005 location permission")

        // ---- C078 · the live map and search ---------------------------------------------
        placeholder(PassengerRoute.LiveMap, "SCR-PA-010 live map")
        placeholder(PassengerRoute.SearchLocation, "SCR-PA-008 search location")

        // ---- C079 · booking --------------------------------------------------------------
        placeholder(PassengerRoute.RideBooking, "SCR-PA-009 ride booking")
        placeholder(PassengerRoute.ProxyRider, "SCR-PA-010b proxy rider details")
        placeholder(PassengerRoute.PackageBooking, "SCR-PA-012 package booking")
        placeholder(PassengerRoute.ScheduleRide, "SCR-PA-013 schedule ride")
        placeholder(PassengerRoute.ConfirmPickup.PATTERN, "SCR-PA-011 confirm pickup")

        // ---- C080 · the ride and its payment ---------------------------------------------
        placeholder(PassengerRoute.FindingDriver.PATTERN, "SCR-PA-014 finding driver")
        placeholder(PassengerRoute.ActiveRide.PATTERN, "SCR-PA-015 ride in progress")
        placeholder(PassengerRoute.PaymentMethod.PATTERN, "SCR-PA-016 payment method")
        placeholder(PassengerRoute.PayFare.PATTERN, "SCR-PA-017 pay fare")
        placeholder(PassengerRoute.TripSummary.PATTERN, "SCR-PA-018 trip summary")
        placeholder(PassengerRoute.RateDriver.PATTERN, "SCR-PA-019 rate driver")

        // ---- C081 · packages and history -------------------------------------------------
        placeholder(PassengerRoute.PackageTracking.PATTERN, "SCR-PA-020/021 package tracking")
        placeholder(PassengerRoute.Trips, "SCR-PA-022 trip & schedule history")
        placeholder(PassengerRoute.TripDetails.PATTERN, "SCR-PA-023 trip details")

        // ---- C082 · Mode B ----------------------------------------------------------------
        // The one destination with an OPTIONAL argument. Declaring it `nullable` with a `null`
        // default is what makes `private-transport` (the drawer row) and
        // `private-transport?vehicleId=…` (a Mode B marker tap) both match this entry — without
        // the declaration the argument-less form does not resolve inside the graph at all.
        composable(
            route = PassengerRoute.ModeBRequest.PATTERN,
            arguments = listOf(
                navArgument(PassengerRoute.ModeBRequest.ARG_VEHICLE_ID) {
                    type = NavType.StringType
                    nullable = true
                    defaultValue = null
                },
            ),
        ) { RoutePlaceholder("SCR-PA-024 Mode B access request") }

        placeholder(PassengerRoute.Subscriptions, "SCR-PA-025 my subscriptions")
        placeholder(PassengerRoute.SubscriptionPayment.PATTERN, "SCR-PA-025a subscription payment")
        placeholder(PassengerRoute.SubscriptionPayments.PATTERN, "SCR-PA-025b payment history")

        // ---- C083 · addresses and settings ------------------------------------------------
        placeholder(PassengerRoute.SavedAddresses, "SCR-PA-026 saved addresses")
        placeholder(PassengerRoute.Settings, "SCR-PA-027 profile & settings")
        placeholder(PassengerRoute.EditProfile, "SCR-PA-027b edit profile")

        // ---- C084 · comms, safety, support -------------------------------------------------
        placeholder(PassengerRoute.VoipCall.PATTERN, "SCR-PA-028 VoIP call")
        placeholder(PassengerRoute.Sos.PATTERN, "SCR-PA-029 SOS")
        placeholder(PassengerRoute.Support, "SCR-PA-030 support + ticket")
    }
}

/** Registers [route] with the standing placeholder. One line per screen the shell is waiting for. */
private fun NavGraphBuilder.placeholder(route: PassengerRoute, screen: String) {
    composable(route.path) { RoutePlaceholder(screen) }
}

/** The same, for a parameterised destination — the pattern is registered, never an instance. */
private fun NavGraphBuilder.placeholder(pattern: String, screen: String) {
    composable(pattern) { RoutePlaceholder(screen) }
}

/**
 * What a registered-but-unbuilt destination shows.
 *
 * Naming the screen id is deliberate: during wave 4a someone will open a tab whose group has not
 * landed, and "SCR-PA-010 live map arrives with its screen group" tells them which prompt owns it.
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
