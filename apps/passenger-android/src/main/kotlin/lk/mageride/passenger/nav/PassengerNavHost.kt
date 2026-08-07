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
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.NavGraphBuilder
import androidx.navigation.NavHostController
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.navArgument
import lk.mageride.passenger.R
import lk.mageride.passenger.booking.BookingDraft
import lk.mageride.passenger.booking.BookingRepository
import lk.mageride.passenger.booking.CaptureTarget
import lk.mageride.passenger.booking.ConfirmPickupScreen
import lk.mageride.passenger.booking.ConfirmPickupViewModel
import lk.mageride.passenger.booking.PackageBookingScreen
import lk.mageride.passenger.booking.PackageEnd
import lk.mageride.passenger.booking.ProxyRiderScreen
import lk.mageride.passenger.booking.RideBookingScreen
import lk.mageride.passenger.booking.ScheduleRideScreen
import lk.mageride.passenger.comms.VoipCallScreen
import lk.mageride.passenger.comms.VoipCallViewModel
import lk.mageride.passenger.comms.VoipEngine
import lk.mageride.passenger.di.PassengerDatabase
import lk.mageride.passenger.history.HistoryRepository
import lk.mageride.passenger.history.PackageOtps
import lk.mageride.passenger.history.PackageTrackScreen
import lk.mageride.passenger.history.PackageTrackViewModel
import lk.mageride.passenger.history.TripDetailsScreen
import lk.mageride.passenger.history.TripDetailsViewModel
import lk.mageride.passenger.history.TripHistoryScreen
import lk.mageride.passenger.history.TripHistoryViewModel
import lk.mageride.passenger.home.LiveMapScreen
import lk.mageride.passenger.home.SearchLocationScreen
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.passenger.location.LastKnownFix
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.passenger.onboarding.LocationPermissionScreen
import lk.mageride.passenger.onboarding.LoginScreen
import lk.mageride.passenger.onboarding.OnboardingScreen
import lk.mageride.passenger.onboarding.PassengerProfileRepository
import lk.mageride.passenger.onboarding.ProfileSetupScreen
import lk.mageride.passenger.onboarding.SplashScreen
import lk.mageride.passenger.ride.ActiveRideScreen
import lk.mageride.passenger.ride.ActiveRideViewModel
import lk.mageride.passenger.ride.CallChoice
import lk.mageride.passenger.ride.FindingDriverScreen
import lk.mageride.passenger.ride.PayFareScreen
import lk.mageride.passenger.ride.PayFareViewModel
import lk.mageride.passenger.ride.PaymentMethodScreen
import lk.mageride.passenger.ride.PaymentMethodViewModel
import lk.mageride.passenger.ride.PaymentSelection
import lk.mageride.passenger.ride.RateDriverScreen
import lk.mageride.passenger.ride.RateDriverViewModel
import lk.mageride.passenger.ride.RideRepository
import lk.mageride.passenger.ride.TripSummaryScreen
import lk.mageride.passenger.ride.TripSummaryViewModel
import lk.mageride.passenger.safety.SosScreen
import lk.mageride.passenger.safety.SosViewModel
import lk.mageride.passenger.settings.AddressBook
import lk.mageride.passenger.settings.EditProfileScreen
import lk.mageride.passenger.settings.EditProfileViewModel
import lk.mageride.passenger.settings.PassengerIdentity
import lk.mageride.passenger.settings.PaymentPreference
import lk.mageride.passenger.settings.SavedAddressesScreen
import lk.mageride.passenger.settings.SavedAddressesViewModel
import lk.mageride.passenger.settings.SettingsScreen
import lk.mageride.passenger.settings.SettingsViewModel
import lk.mageride.passenger.settings.SosContacts
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.passenger.subscription.ModeBRequestScreen
import lk.mageride.passenger.subscription.ModeBRequestViewModel
import lk.mageride.passenger.subscription.SubscriptionPayScreen
import lk.mageride.passenger.subscription.SubscriptionPayViewModel
import lk.mageride.passenger.subscription.SubscriptionPaymentsScreen
import lk.mageride.passenger.subscription.SubscriptionPaymentsViewModel
import lk.mageride.passenger.subscription.SubscriptionRepository
import lk.mageride.passenger.subscription.SubscriptionsScreen
import lk.mageride.passenger.subscription.SubscriptionsViewModel
import lk.mageride.passenger.support.SupportRepository
import lk.mageride.passenger.support.SupportScreen
import lk.mageride.passenger.support.SupportViewModel
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState
import org.koin.compose.koinInject

/**
 * The app's single `NavHost`, with one entry per [PassengerRoute].
 *
 * **Every destination is registered here, and a screen group replaces the body of its own routes
 * without touching the graph.** A component that added a destination of its own would put the
 * app's navigation in eight files. C077–C084 took their routes one cluster at a time, and with
 * C084's SCR-PA-028/029/030 the last of them landed: **there is no placeholder left in this file**,
 * and every route below draws a real screen.
 *
 * The start destination is [PassengerRoute.Splash] — SCR-PA-001 is the session router, and its
 * states (*"KMP auth validates token → onboarding / login / live_map, resumes an active ride via
 * `GET /v1/rides/passenger/{id}/active`"*) are exactly why the first screen cannot be a tab.
 */
@Composable
internal fun PassengerNavHost(controller: NavHostController, modifier: Modifier = Modifier) {
    // Resolved once for the graph rather than per destination. These three are what the booking
    // cluster needs *at the navigation seam* — the draft, because SCR-PA-008 is one picker serving
    // five callers (see `CaptureTarget`), and the repository and location source, because
    // SCR-PA-011's view model takes a navigation argument and is built here.
    val draft = koinInject<BookingDraft>()
    val bookings = koinInject<BookingRepository>()
    val locations = koinInject<PassengerLocationSource>()
    val lastFix = koinInject<LastKnownFix>()

    // C080's cluster. Every screen below is scoped to one ride and takes its id from the route,
    // so the view models are built here rather than resolved with `parametersOf`.
    val rideRepository = koinInject<RideRepository>()
    val callChoice = koinInject<CallChoice>()
    val paymentSelection = koinInject<PaymentSelection>()
    val sessions = koinInject<AuthSessionManager>()
    val live = koinInject<PassengerLiveMap>()
    val databases = koinInject<PassengerDatabase>()

    // C081's cluster.
    val historyRepository = koinInject<HistoryRepository>()
    val packageOtps = koinInject<PackageOtps>()

    // C082's cluster. Its four view models take a vehicle or a subscription id from the route, so
    // they are built here for the reason C080's are.
    val subscriptionRepository = koinInject<SubscriptionRepository>()
    val idempotencyKeys = koinInject<IdempotencyKeyGenerator>()

    // C084's cluster. SCR-PA-028 and SCR-PA-029 are ride-scoped and take their id from the route;
    // SCR-PA-030 is a tab and takes none, and is built here for the reason C083's three are.
    val voipEngine = koinInject<VoipEngine>()
    val supportRepository = koinInject<SupportRepository>()

    // C083's cluster. None of its three screens takes a navigation argument, so these could have
    // been `viewModel { }` bindings — they are resolved here for the same reason C082's SCR-PA-025
    // is: the graph already resolves each of these singles once, and building the models beside
    // the destinations keeps every screen group's wiring in one readable place.
    val addressBook = koinInject<AddressBook>()
    val sosContacts = koinInject<SosContacts>()
    val identity = koinInject<PassengerIdentity>()
    val paymentPreference = koinInject<PaymentPreference>()
    val profiles = koinInject<PassengerProfileRepository>()
    val preferences = koinInject<AppPreferences>()
    val signedInUserId = (sessions.state.collectAsStateWithLifecycle().value as? SessionState.SignedIn)?.userId

    NavHost(
        navController = controller,
        startDestination = PassengerRoute.Splash.path,
        modifier = modifier,
    ) {
        // ---- C077 · auth / onboarding --------------------------------------------------
        composable(PassengerRoute.Splash.path) {
            SplashScreen(onResolved = { controller.replaceOnboarding(it) })
        }
        composable(PassengerRoute.Onboarding.path) {
            // The view model is scoped to this entry rather than injected fresh per composition:
            // `finish()` compares against the language the screen was ENTERED in, and a new
            // instance mid-screen would read the value it had just written. See its KDoc.
            OnboardingScreen(
                onContinue = { controller.replaceOnboarding(PassengerRoute.Login) },
                model = org.koin.androidx.compose.koinViewModel(),
            )
        }
        composable(PassengerRoute.Login.path) {
            LoginScreen(
                onSignedIn = { controller.replaceOnboarding(it.route) },
                onBack = { controller.popBackStack() },
            )
        }
        composable(PassengerRoute.ProfileSetup.path) {
            ProfileSetupScreen(
                onSaved = { controller.replaceOnboarding(PassengerRoute.LocationPermission) },
                onBack = { controller.popBackStack() },
            )
        }
        composable(PassengerRoute.LocationPermission.path) {
            LocationPermissionScreen(onContinue = { controller.replaceOnboarding(PassengerRoute.LiveMap) })
        }

        // ---- C078 · the live map and search ---------------------------------------------
        composable(PassengerRoute.LiveMap.path) {
            LiveMapScreen(
                onSearch = { controller.navigate(PassengerRoute.SearchLocation.path) },
                // AL-23 / US-4.6 — a Mode B marker opens the access request with the vehicle
                // already filled in. SCR-PA-007's popup is NOT what a private vehicle offers.
                onRequestModeBAccess = { vehicleId ->
                    controller.navigate(PassengerRoute.ModeBRequest(vehicleId).path)
                },
                // A shortcut and a recent are both destinations, so they go where a chosen place
                // goes: straight into a booking. Tapping ★ Home IS choosing where to go.
                onOpenSavedAddress = { address ->
                    draft.begin(Place(lat = address.lat, lng = address.lng, address = address.label))
                    controller.navigate(PassengerRoute.RideBooking.path)
                },
                onOpenRecent = { place ->
                    draft.begin(place.toPlace())
                    controller.navigate(PassengerRoute.RideBooking.path)
                },
                onAddAddress = { controller.navigate(PassengerRoute.SavedAddresses.path) },
            )
        }
        composable(PassengerRoute.SearchLocation.path) {
            SearchLocationScreen(
                // Biased toward the passenger (Δ C097). The map records what it already collects
                // and this reads it — a second collector would hold the fused provider open behind
                // a screen that only needs one reading. See `LastKnownFix`.
                around = lastFix.point,
                onBack = { controller.popBackStack() },
                // ONE picker, five callers. Whoever opened it parked a `CaptureTarget` on the
                // draft; if nobody did, this is the home sheet's "Where to?" and the chosen place
                // begins a booking rather than editing one. See `CaptureTarget`.
                onPlaceChosen = { place ->
                    if (draft.capture(place.toPlace())) {
                        controller.popBackStack()
                    } else {
                        draft.begin(place.toPlace())
                        controller.navigate(PassengerRoute.RideBooking.path) {
                            popUpTo(PassengerRoute.SearchLocation.path) { inclusive = true }
                        }
                    }
                },
                onPickOnMap = { controller.popBackStack() },
                onAddAddress = { controller.navigate(PassengerRoute.SavedAddresses.path) },
            )
        }

        // ---- C079 · booking --------------------------------------------------------------
        composable(PassengerRoute.RideBooking.path) {
            RideBookingScreen(
                onBack = { controller.popBackStack() },
                onEditRoute = {
                    draft.expect(CaptureTarget.BOOKING_DROPOFF)
                    controller.navigate(PassengerRoute.SearchLocation.path)
                },
                onProxyDetails = { controller.navigate(PassengerRoute.ProxyRider.path) },
                onPackageBooking = { controller.navigate(PassengerRoute.PackageBooking.path) },
                onSchedule = { controller.navigate(PassengerRoute.ScheduleRide.path) },
                // C080 owns SCR-PA-014; the ride exists from `POST /v1/rides/request` on, so the
                // route is parameterised and correct even though the screen is still a placeholder.
                onBooked = { rideId ->
                    controller.navigate(PassengerRoute.FindingDriver(rideId).path) {
                        popUpTo(PassengerRoute.LiveMap.path)
                    }
                },
                // "Track Route" — the map follows the GTFS polyline. SCR-PA-010 is where a route
                // is watched, and the line is already drawn on the booking map behind this sheet.
                onTrackRoute = {
                    controller.navigate(PassengerRoute.LiveMap.path) {
                        popUpTo(PassengerRoute.LiveMap.path) { inclusive = true }
                    }
                },
            )
        }

        composable(PassengerRoute.ProxyRider.path) {
            ProxyRiderScreen(
                onBack = { controller.popBackStack() },
                onSearch = {
                    draft.expect(CaptureTarget.PROXY_PICKUP)
                    controller.navigate(PassengerRoute.SearchLocation.path)
                },
                onDone = { controller.popBackStack() },
            )
        }

        composable(PassengerRoute.PackageBooking.path) {
            PackageBookingScreen(
                onBack = { controller.popBackStack() },
                onSearch = { end ->
                    draft.expect(
                        if (end == PackageEnd.PICKUP) CaptureTarget.PACKAGE_PICKUP else CaptureTarget.PACKAGE_DROPOFF,
                    )
                    controller.navigate(PassengerRoute.SearchLocation.path)
                },
                onBooked = { rideId ->
                    controller.navigate(PassengerRoute.PackageTracking(rideId).path) {
                        popUpTo(PassengerRoute.LiveMap.path)
                    }
                },
            )
        }

        composable(PassengerRoute.ScheduleRide.path) {
            ScheduleRideScreen(
                onBack = { controller.popBackStack() },
                onPickDestination = {
                    draft.expect(CaptureTarget.SCHEDULE_DROPOFF)
                    controller.navigate(PassengerRoute.SearchLocation.path)
                },
                // A scheduled ride is not a ride yet — it is a `dispatch.scheduled_rides` row that
                // materialises at T-30. C081's SCR-PA-022 is where it is seen; until then the
                // passenger returns to the map, which is where they started.
                onScheduled = {
                    controller.navigate(PassengerRoute.LiveMap.path) {
                        popUpTo(PassengerRoute.LiveMap.path) { inclusive = true }
                    }
                },
            )
        }

        composable(
            route = PassengerRoute.ConfirmPickup.PATTERN,
            arguments = listOf(navArgument(PassengerRoute.ConfirmPickup.ARG_REQUEST_ID) { type = NavType.StringType }),
        ) { entry ->
            val requestId = entry.arguments?.getString(PassengerRoute.ConfirmPickup.ARG_REQUEST_ID).orEmpty()
            ConfirmPickupScreen(
                onFinished = { controller.popBackStack() },
                // Built here rather than resolved from Koin: the request id is a navigation
                // argument, and `parametersOf` would put a runtime cast between the route and the
                // screen for no gain. Keyed on the id so a second push replaces the model.
                model = viewModel(key = requestId) {
                    ConfirmPickupViewModel(requestId = requestId, bookings = bookings, locations = locations)
                },
            )
        }

        // ---- C080 · the ride and its payment ---------------------------------------------
        rideScoped(PassengerRoute.FindingDriver.PATTERN, PassengerRoute.FindingDriver.ARG_RIDE_ID) { rideId ->
            FindingDriverScreen(
                // Accepted — SCR-PA-015 REPLACES this screen rather than stacking over it, so a
                // back gesture from an active ride cannot land on a search that already ended.
                onAssigned = {
                    controller.navigate(PassengerRoute.ActiveRide(rideId).path) {
                        popUpTo(PassengerRoute.FindingDriver.PATTERN) { inclusive = true }
                    }
                },
                onCancelled = { controller.popBackStack(PassengerRoute.LiveMap.path, inclusive = false) },
                onRetry = { controller.popBackStack(PassengerRoute.LiveMap.path, inclusive = false) },
                model = viewModel(key = rideId) { ActiveRideViewModel(rideId, rideRepository, live) },
            )
        }

        rideScoped(PassengerRoute.ActiveRide.PATTERN, PassengerRoute.ActiveRide.ARG_RIDE_ID) { rideId ->
            ActiveRideScreen(
                onFinished = { controller.popBackStack(PassengerRoute.LiveMap.path, inclusive = false) },
                // Δ C098 — the hand-off this screen's own KDoc described and nothing implemented.
                // A REPLACE, not a push: a completed ride must not be one back gesture from a screen
                // that is still watching it.
                onPayFare = {
                    controller.navigate(PassengerRoute.PaymentMethod(rideId).path) {
                        popUpTo(PassengerRoute.ActiveRide.PATTERN) { inclusive = true }
                    }
                },
                onReceipt = {
                    controller.navigate(PassengerRoute.TripSummary(rideId).path) {
                        popUpTo(PassengerRoute.ActiveRide.PATTERN) { inclusive = true }
                    }
                },
                onFreeCall = { controller.navigate(PassengerRoute.VoipCall(rideId).path) },
                // `⛨ SOS` NAVIGATES; it does not raise the alarm (Δ C084). SCR-PA-029 is where the
                // confirm, the countdown and the dispatched state live, and one door to
                // `POST /v1/sos` is what stops one emergency becoming two rows on the operator feed.
                onSos = { controller.navigate(PassengerRoute.Sos(rideId).path) },
                choice = callChoice,
                model = viewModel(key = rideId) { ActiveRideViewModel(rideId, rideRepository, live) },
            )
        }

        rideScoped(PassengerRoute.PaymentMethod.PATTERN, PassengerRoute.PaymentMethod.ARG_RIDE_ID) { rideId ->
            PaymentMethodScreen(
                onConfirmed = { controller.navigate(PassengerRoute.PayFare(rideId).path) },
                // **There is no passenger wallet screen, anywhere** (Δ C082). C080 parked this on
                // `Subscriptions` while that route was a placeholder; it is now SCR-PA-025 and
                // sending a passenger who is Rs 40 short of a fare to their Mode B subscriptions
                // would be worse than doing nothing. No SCR-PA id, no wireframe and no D2' section
                // draws a top-up, even though AL-57 made the wallet the card-acceptance rail — the
                // gap is recorded in the C082 handoff, and this is the one line that changes when
                // the screen exists.
                onTopUp = { },
                model = viewModel(key = rideId) {
                    PaymentMethodViewModel(rideId, rideRepository, sessions, paymentPreference, paymentSelection)
                },
            )
        }

        rideScoped(PassengerRoute.PayFare.PATTERN, PassengerRoute.PayFare.ARG_RIDE_ID) { rideId ->
            PayFareScreen(
                onBack = { controller.popBackStack() },
                onSupport = { controller.navigate(PassengerRoute.Support.path) },
                onSettled = {
                    paymentSelection.forget(rideId)
                    controller.navigate(PassengerRoute.TripSummary(rideId).path) {
                        popUpTo(PassengerRoute.ActiveRide.PATTERN) { inclusive = true }
                    }
                },
                // Δ C098 — **the rail SCR-PA-016 confirmed**. This used to be the default
                // (`scan_driver_qr`) whatever the passenger chose, because `onConfirmed`'s argument
                // was discarded one arm up and `setMethod` had no caller. See `PaymentSelection`.
                model = viewModel(key = rideId) {
                    PayFareViewModel(rideId, rideRepository, paymentSelection.railFor(rideId))
                },
            )
        }

        rideScoped(PassengerRoute.TripSummary.PATTERN, PassengerRoute.TripSummary.ARG_RIDE_ID) { rideId ->
            TripSummaryScreen(
                onClose = { controller.popBackStack(PassengerRoute.LiveMap.path, inclusive = false) },
                onPay = { controller.navigate(PassengerRoute.PaymentMethod(rideId).path) },
                onRate = { controller.navigate(PassengerRoute.RateDriver(rideId).path) },
                model = viewModel(key = rideId) { TripSummaryViewModel(rideId, rideRepository) },
            )
        }

        rideScoped(PassengerRoute.RateDriver.PATTERN, PassengerRoute.RateDriver.ARG_RIDE_ID) { rideId ->
            RateDriverScreen(
                onDone = { controller.popBackStack(PassengerRoute.LiveMap.path, inclusive = false) },
                model = viewModel(key = rideId) { RateDriverViewModel(rideId, rideRepository, databases) },
            )
        }

        // ---- C081 · packages and history -------------------------------------------------
        rideScoped(PassengerRoute.PackageTracking.PATTERN, PassengerRoute.PackageTracking.ARG_RIDE_ID) { rideId ->
            PackageTrackScreen(
                onBack = { controller.popBackStack() },
                onFreeCall = { controller.navigate(PassengerRoute.VoipCall(rideId).path) },
                choice = callChoice,
                model = viewModel(key = rideId) {
                    PackageTrackViewModel(
                        rideId = rideId,
                        history = historyRepository,
                        live = live,
                        otps = packageOtps,
                        // SCR-PA-020 or SCR-PA-021: the sender is the ride's booker. A recipient
                        // arrived from an FCM deep link and has no booking of their own, so the
                        // absence of a match is the answer rather than a missing case.
                        isSender = { ride -> ride.bookerId != null && ride.bookerId == signedInUserId },
                    )
                },
            )
        }

        composable(PassengerRoute.Trips.path) {
            TripHistoryScreen(
                onOpenTrip = { tripId -> controller.navigate(PassengerRoute.TripDetails(tripId).path) },
                onFreeCall = { },
                choice = callChoice,
                model = viewModel { TripHistoryViewModel(historyRepository, sessions) },
            )
        }

        composable(
            route = PassengerRoute.TripDetails.PATTERN,
            arguments = listOf(navArgument(PassengerRoute.TripDetails.ARG_TRIP_ID) { type = NavType.StringType }),
        ) { entry ->
            val tripId = entry.arguments?.getString(PassengerRoute.TripDetails.ARG_TRIP_ID).orEmpty()
            TripDetailsScreen(
                onBack = { controller.popBackStack() },
                onReportIssue = { controller.navigate(PassengerRoute.Support.path) },
                model = viewModel(key = tripId) { TripDetailsViewModel(tripId, historyRepository, sessions) },
            )
        }

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
        ) { entry ->
            val vehicleId = entry.arguments?.getString(PassengerRoute.ModeBRequest.ARG_VEHICLE_ID)
            ModeBRequestScreen(
                onBack = { controller.popBackStack() },
                onOpenSubscriptions = { controller.navigate(PassengerRoute.Subscriptions.path) },
                // Keyed on the id so the drawer's argument-less entry and a marker tap do not
                // share one model — the second would otherwise open pre-filled with the first's
                // vehicle, which is the one mistake this screen must not make.
                model = viewModel(key = vehicleId.orEmpty()) {
                    ModeBRequestViewModel(
                        vehicleId = vehicleId,
                        subscriptions = subscriptionRepository,
                        sessions = sessions,
                        keys = idempotencyKeys,
                    )
                },
            )
        }

        composable(PassengerRoute.Subscriptions.path) {
            SubscriptionsScreen(
                onBack = { controller.popBackStack() },
                onPay = { id -> controller.navigate(PassengerRoute.SubscriptionPayment(id).path) },
                onHistory = { id -> controller.navigate(PassengerRoute.SubscriptionPayments(id).path) },
                model = viewModel {
                    SubscriptionsViewModel(
                        subscriptions = subscriptionRepository,
                        sessions = sessions,
                        // AL-25's other half: the grant is gone, so the marker goes with it
                        // rather than waiting for `ShareRevoked` to come back.
                        live = live,
                        keys = idempotencyKeys,
                    )
                },
            )
        }

        subscriptionScoped(
            PassengerRoute.SubscriptionPayment.PATTERN,
            PassengerRoute.SubscriptionPayment.ARG_SUBSCRIPTION_ID,
        ) { subscriptionId ->
            SubscriptionPayScreen(
                onBack = { controller.popBackStack() },
                onDone = { controller.popBackStack() },
                model = viewModel(key = subscriptionId) {
                    SubscriptionPayViewModel(
                        subscriptionId = subscriptionId,
                        subscriptions = subscriptionRepository,
                        sessions = sessions,
                        keys = idempotencyKeys,
                    )
                },
            )
        }

        subscriptionScoped(
            PassengerRoute.SubscriptionPayments.PATTERN,
            PassengerRoute.SubscriptionPayments.ARG_SUBSCRIPTION_ID,
        ) { subscriptionId ->
            SubscriptionPaymentsScreen(
                onBack = { controller.popBackStack() },
                model = viewModel(key = subscriptionId) {
                    SubscriptionPaymentsViewModel(
                        subscriptionId = subscriptionId,
                        subscriptions = subscriptionRepository,
                        sessions = sessions,
                    )
                },
            )
        }

        // ---- C083 · addresses and settings ------------------------------------------------
        composable(PassengerRoute.SavedAddresses.path) {
            SavedAddressesScreen(
                onBack = { controller.popBackStack() },
                model = viewModel { SavedAddressesViewModel(addressBook, locations, idempotencyKeys) },
            )
        }

        composable(PassengerRoute.Settings.path) {
            SettingsScreen(
                onBack = { controller.popBackStack() },
                onEditProfile = { controller.navigate(PassengerRoute.EditProfile.path) },
                // Both of the wireframe's address rows land here — "Save Home & Work" and "Saved
                // addresses" are two intentions onto one screen, which is why SCR-PA-026 draws the
                // two shortcut rows above everything else.
                onSavedAddresses = { controller.navigate(PassengerRoute.SavedAddresses.path) },
                onSupport = { controller.navigate(PassengerRoute.Support.path) },
                model = viewModel {
                    SettingsViewModel(
                        profiles = profiles,
                        identity = identity,
                        payments = paymentPreference,
                        preferences = preferences,
                        sessions = sessions,
                    )
                },
            )
        }

        composable(PassengerRoute.EditProfile.path) {
            EditProfileScreen(
                onBack = { controller.popBackStack() },
                model = viewModel { EditProfileViewModel(profiles, sosContacts, identity, idempotencyKeys) },
            )
        }

        // ---- C084 · comms, safety, support -------------------------------------------------
        rideScoped(PassengerRoute.VoipCall.PATTERN, PassengerRoute.VoipCall.ARG_RIDE_ID) { rideId ->
            VoipCallScreen(
                // Hung up, dialled normally, or dismissed — every exit is the same one, and it goes
                // back to whatever opened the call rather than to a fixed screen: SCR-PA-015a is
                // reachable from the active ride, from a package track and from trip history.
                onFinished = { controller.popBackStack() },
                model = viewModel(key = rideId) { VoipCallViewModel(rideId, rideRepository, voipEngine) },
            )
        }

        rideScoped(PassengerRoute.Sos.PATTERN, PassengerRoute.Sos.ARG_RIDE_ID) { rideId ->
            SosScreen(
                onFinished = { controller.popBackStack() },
                model = viewModel(key = rideId) {
                    SosViewModel(rideId, rideRepository, sosContacts, locations)
                },
            )
        }

        composable(PassengerRoute.Support.path) {
            SupportScreen(
                onBack = { controller.popBackStack() },
                model = viewModel { SupportViewModel(supportRepository, sessions, preferences) },
            )
        }
    }
}

/**
 * Moves forward through onboarding, leaving nothing behind.
 *
 * Every step of cluster 1 is a one-way door: Back from Login must leave the app rather than return
 * to the splash, Back from Profile Setup must not return to the OTP screen of a session that
 * already exists, and Back from the map must never re-enter onboarding at all. Popping the whole
 * graph on each step is what makes all three true with one rule.
 */
private fun NavHostController.replaceOnboarding(route: PassengerRoute) {
    navigate(route.path) {
        popUpTo(graph.id) { inclusive = true }
        launchSingleTop = true
    }
}

/**
 * A destination scoped to one ride.
 *
 * Six of C080's screens take a `rideId` from the path and nothing else, and writing the
 * `navArgument` block six times would be six chances to type the wrong argument name. The lambda
 * receives the id already extracted.
 */
private fun NavGraphBuilder.rideScoped(pattern: String, argument: String, content: @Composable (String) -> Unit) {
    composable(route = pattern, arguments = listOf(navArgument(argument) { type = NavType.StringType })) { entry ->
        content(entry.arguments?.getString(argument).orEmpty())
    }
}

/**
 * A destination scoped to one Mode B subscription (C082).
 *
 * The same shape as [rideScoped] with a different argument name, kept separate rather than
 * generalised: the two argument constants are what a mistyped route would collide on, and a single
 * helper taking both would make `ride/{rideId}` and `subscriptions/{subscriptionId}` look
 * interchangeable at the call site.
 */
private fun NavGraphBuilder.subscriptionScoped(
    pattern: String,
    argument: String,
    content: @Composable (String) -> Unit,
) {
    composable(route = pattern, arguments = listOf(navArgument(argument) { type = NavType.StringType })) { entry ->
        content(entry.arguments?.getString(argument).orEmpty())
    }
}

// The standing `RoutePlaceholder` the shell shipped is GONE (Δ C084), along with its two
// `NavGraphBuilder.placeholder(…)` helpers and the `route_placeholder_*` strings. It existed so
// that during wave 4a somebody opening a tab whose screen group had not landed was told which
// prompt owned it; C084 took the last three (SCR-PA-028/029/030), so every destination in this
// graph now registers a real composable and nothing is waiting. Re-adding it would be a way to
// register a route with no screen behind it.
