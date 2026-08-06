package lk.mageride.passenger.nav

import lk.mageride.passenger.push.PushMessage
import lk.mageride.passenger.push.PushRouter
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The shell's navigation contracts: the four tabs, SCR-PA-033's drawer, the route table, and the
 * deep links C051 mints.
 *
 * All four are the kind of thing that is correct on the day it is written and quietly wrong six
 * components later — a drawer row pointed at the nearest existing screen, a `mageride://` host
 * added on the server with no client route, two destinations that collide on one pattern.
 * Asserting the tables is what makes any of those a build failure rather than a bug report.
 */
class NavigationShellTest {

    @Test
    fun the_bottom_navigation_is_the_wireframes_four_tabs_in_order() {
        assertEquals(
            listOf(
                PassengerTab.MapTab,
                PassengerTab.TripsTab,
                PassengerTab.SupportTab,
                PassengerTab.MenuTab,
            ),
            PassengerTab.entries,
            "passenger_android.html's [Map][Trips][Support][Menu]",
        )
    }

    @Test
    fun the_menu_tab_opens_the_drawer_and_is_not_a_destination() {
        // SCR-PA-033: "a Material 3 modal navigation drawer opened from the ≡ menu / 'Menu' tab".
        // The wireframe draws the map still underneath with the scrim over it — so the Menu tab
        // navigates nowhere, and a route for it would put a back-stack entry behind a sheet the
        // passenger dismisses by tapping the scrim.
        assertNull(PassengerTab.MenuTab.route, "the Menu tab must not name a destination")
        assertEquals(3, PassengerTab.destinations.size, "three tabs are destinations, the fourth is the drawer")
    }

    @Test
    fun the_bottom_bar_shows_on_tabs_and_nowhere_else() {
        PassengerTab.destinations.forEach { assertTrue(isTabRoute(it.path), "${it.path} is a tab") }

        // Onboarding is full-bleed in the wireframes, and so is every takeover: the QR camera, the
        // call and the alarm draw no navbar at all.
        listOf(
            PassengerRoute.Splash,
            PassengerRoute.Onboarding,
            PassengerRoute.Login,
            PassengerRoute.ProfileSetup,
            PassengerRoute.LocationPermission,
            PassengerRoute.RideBooking,
        ).forEach { assertTrue(!isTabRoute(it.path), "${it.path} must not show the bottom bar") }

        assertTrue(!isTabRoute(PassengerRoute.PayFare("01JRIDE0000000000000000000").path))
        assertTrue(!isTabRoute(PassengerRoute.Sos("01JRIDE0000000000000000000").path))
        assertTrue(!isTabRoute(null), "an unresolved route shows no bar")
    }

    @Test
    fun the_drawer_is_the_wireframes_five_destinations_plus_log_out() {
        // "Items route to: Private transport → SCR-PA-024, My subscriptions → SCR-PA-025, Saved
        // addresses → SCR-PA-026, Profile & settings → SCR-PA-027 (plus Help & support, Log out)."
        assertEquals(
            listOf(
                PassengerRoute.ModeBRequest(),
                PassengerRoute.Subscriptions,
                PassengerRoute.SavedAddresses,
                PassengerRoute.Settings,
                PassengerRoute.Support,
            ),
            PassengerDrawerDestination.entries.map(PassengerDrawerDestination::route),
            "the drawer's five rows, in the order the wireframe prints them",
        )

        // The divider's two sides. Log out is neither — it is an action, and it is the shell's.
        assertEquals(4, PassengerDrawerDestination.primary.size)
        assertEquals(1, PassengerDrawerDestination.secondary.size)
    }

    @Test
    fun the_private_transport_row_carries_no_vehicle_id() {
        // AL-23: opened from a Mode B marker the request arrives pre-filled; opened from the
        // drawer there is no marker to read one from and the passenger types it. The route is the
        // same destination either way, which is what the optional argument exists for.
        val vehicleId = "01JVEH0000000000000000000"

        assertEquals("private-transport", PassengerRoute.ModeBRequest().path)
        assertEquals("private-transport?vehicleId=$vehicleId", PassengerRoute.ModeBRequest(vehicleId).path)
        assertEquals("private-transport?vehicleId={vehicleId}", PassengerRoute.ModeBRequest.PATTERN)
    }

    @Test
    fun every_registered_route_has_a_distinct_pattern() {
        val patterns = PassengerRoute.Static.map(PassengerRoute::path) + PassengerRoute.Patterns
        assertEquals(patterns.distinct(), patterns, "two destinations share a route pattern")
        assertTrue(patterns.none(String::isBlank), "a destination has no path")
    }

    @Test
    fun the_two_deep_links_notification_svc_addresses_to_a_passenger_resolve_to_screens() {
        // `DeepLinks` in Notification.Api/Messaging/EventHandlers.cs mints four URIs. Two are the
        // passenger's; D6' I-23.3 prints the package one and names SCR-PA-021 as its target.
        assertEquals(
            PassengerRoute.ActiveRide("01JRIDE0000000000000000000"),
            PushRouter.resolve("mageride://ride/01JRIDE0000000000000000000"),
        )
        assertEquals(
            PassengerRoute.PackageTracking("01JRIDE0000000000000000000"),
            PushRouter.resolve("mageride://package/01JRIDE0000000000000000000"),
            "a parcel has its own screens on this side (SCR-PA-020/021), unlike the driver's",
        )
    }

    @Test
    fun the_drivers_two_deep_links_open_nothing_here() {
        // `mageride://wallet` and `mageride://documents` are LOW_BALANCE, TOP_UP_REQUIRED,
        // DAILY_FEE and the E-03 document warnings — every one of them addressed to a driver.
        // Resolving them to "the nearest passenger screen" would open something arbitrary the
        // first time an operator mis-targeted a broadcast.
        assertNull(PushRouter.resolve("mageride://wallet"))
        assertNull(PushRouter.resolve("mageride://documents"))
    }

    @Test
    fun an_unknown_or_hostile_link_resolves_to_nothing() {
        // A deeplink arrives over the network. Resolving against the known table rather than
        // handing the URI to the navigator is what keeps an unrecognised value from opening
        // anything at all.
        assertNull(PushRouter.resolve(null))
        assertNull(PushRouter.resolve(""))
        assertNull(PushRouter.resolve("https://mageride.lk/ride/1"), "wrong scheme")
        assertNull(PushRouter.resolve("mageride://"), "no host")
        assertNull(PushRouter.resolve("mageride://admin/users"), "host with no screen")
        assertNull(PushRouter.resolve("mageride://ride/"), "ride with no id")
    }

    @Test
    fun a_location_request_push_opens_the_confirm_screen_from_its_own_payload() {
        // P-02's silent data message is `{kind:'location_request', requestId, bookerName, ttl:300}`
        // — no `deeplink` at all, because notification-svc mints no URI for it. The route is built
        // from `requestId`; inventing a `mageride://pickup-confirm/{id}` host would be the client
        // claiming a link the server does not send.
        val request = PushMessage.from(
            mapOf(
                "kind" to "location_request",
                "requestId" to "01JLOCREQ00000000000000000",
                "ttl" to "300",
            ),
        )

        assertEquals(
            PassengerRoute.ConfirmPickup("01JLOCREQ00000000000000000"),
            PushRouter.routeFor(request),
        )
        assertNull(PushRouter.resolve("mageride://pickup-confirm/01JLOCREQ00000000000000000"))
    }

    @Test
    fun a_location_request_with_no_id_opens_nothing() {
        val malformed = PushMessage.from(mapOf("kind" to "location_request", "ttl" to "300"))
        assertNull(PushRouter.routeFor(malformed), "a request with no id has no screen to open")
    }

    @Test
    fun the_thirty_minute_scheduled_reminder_opens_the_trips_tab() {
        // US-6A.15 / US-10.9. It carries no deeplink either — `DeepLinks` mints four URIs and none
        // names a scheduled ride — so the routing is on the type name D5' §14.4 and
        // `NotificationCatalogue` both fix. SCR-PA-022's "Scheduled" tab is where it lives here.
        val reminder = PushMessage.from(
            mapOf("kind" to "SCHEDULED_REMINDER", "notificationId" to "01JNOTIF000000000000000000"),
        )

        assertEquals(PassengerRoute.Trips, PushRouter.routeFor(reminder))
        assertNull(PushRouter.resolve("mageride://scheduled"), "there is no such host, and none is invented")
    }

    @Test
    fun a_ride_push_follows_its_deeplink() {
        // DRIVER_ASSIGNED, DRIVER_ARRIVED, RIDE_CANCELLED and PAYMENT_CONFIRMED all carry
        // `DeepLinks.Ride(rideId)`, so they need no per-kind branch — the URI is enough.
        val assigned = PushMessage.from(
            mapOf(
                "kind" to "DRIVER_ASSIGNED",
                "deeplink" to "mageride://ride/01JRIDE0000000000000000000",
                "notificationId" to "01JNOTIF000000000000000000",
            ),
        )

        assertEquals(PassengerRoute.ActiveRide("01JRIDE0000000000000000000"), PushRouter.routeFor(assigned))
    }

    @Test
    fun a_push_with_no_link_opens_nothing() {
        val announcement = PushMessage.from(mapOf("kind" to "BROADCAST"))
        assertNull(PushRouter.routeFor(announcement))
    }
}
