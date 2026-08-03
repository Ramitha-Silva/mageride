package lk.mageride.driver.nav

import lk.mageride.driver.push.PushMessage
import lk.mageride.driver.push.PushRouter
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The shell's two navigation contracts: AL-31's bottom navigation, and the deep links C051 mints.
 *
 * Both are the kind of thing that is correct on the day it is written and quietly wrong six
 * components later — a drawer reintroduced by a screen group that wanted one, a `mageride://` host
 * added on the server with no client route. Asserting the tables is what makes either a build
 * failure rather than a bug report.
 */
class NavigationShellTest {

    @Test
    fun the_bottom_navigation_is_the_four_tabs_and_menu_is_one_of_them() {
        // AL-31: "The dashboard has NO top-left hamburger; navigation is the bottom-nav Menu tab."
        // The Menu TAB is the drawer's whole replacement, so it has to be a peer of the other
        // three rather than a corner affordance.
        assertEquals(
            listOf(DriverRoute.Home, DriverRoute.Jobs, DriverRoute.Wallet, DriverRoute.Menu),
            DriverTab.entries.map(DriverTab::route),
            "the driver_android.html wireframe's [Home][Jobs][Wallet][≡], in order",
        )
        assertTrue(DriverTab.entries.any { it.route == DriverRoute.Menu }, "AL-31's Menu tab")
    }

    @Test
    fun the_bottom_bar_shows_on_tabs_and_nowhere_else() {
        DriverTab.entries.forEach { assertTrue(isTabRoute(it.route.path), "${it.route.path} is a tab") }

        // Cluster 1 is full-bleed in the wireframes — no navbar is drawn on any of it, and the
        // document camera is a viewfinder.
        listOf(
            DriverRoute.Splash,
            DriverRoute.LanguageCity,
            DriverRoute.Login,
            DriverRoute.ProfileSetup,
            DriverRoute.Permissions,
            DriverRoute.DocumentCapture,
        ).forEach { assertTrue(!isTabRoute(it.path), "${it.path} must not show the bottom bar") }

        assertTrue(!isTabRoute(null), "an unresolved route shows no bar")
    }

    @Test
    fun every_static_route_has_a_distinct_path() {
        val paths = DriverRoute.Static.map(DriverRoute::path)
        assertEquals(paths.distinct(), paths, "two destinations share a route pattern")
        assertTrue(paths.none(String::isBlank), "a destination has no path")
    }

    @Test
    fun the_four_deep_links_notification_svc_mints_resolve_to_screens() {
        // `DeepLinks` in Notification.Api/Messaging/EventHandlers.cs — the only four it produces.
        // D6' I-23.3 prints the package one; C051 note (n) records the other three.
        assertEquals(
            DriverRoute.ActiveRide("01JRIDE0000000000000000000"),
            PushRouter.resolve("mageride://ride/01JRIDE0000000000000000000"),
        )
        assertEquals(
            DriverRoute.ActiveRide("01JRIDE0000000000000000000"),
            PushRouter.resolve("mageride://package/01JRIDE0000000000000000000"),
            "a parcel is an ordinary ride on the driver side (R-01)",
        )
        assertEquals(DriverRoute.Wallet, PushRouter.resolve("mageride://wallet"))
        assertEquals(DriverRoute.Documents, PushRouter.resolve("mageride://documents"))
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
    fun a_ride_offer_push_opens_home_whatever_deeplink_it_carries() {
        // E-01's offer is a 15 s takeover the dashboard owns (C070), not a screen of its own —
        // and `offer.created` carries the passenger-side ride link, which a driver must not follow.
        val offer = PushMessage.from(
            mapOf(
                "kind" to "ride_offer",
                "deeplink" to "mageride://ride/01JRIDE0000000000000000000",
                "notificationId" to "01JNOTIF000000000000000000",
            ),
        )

        assertEquals(DriverRoute.Home, PushRouter.routeFor(offer))
    }

    @Test
    fun a_push_with_no_link_opens_nothing() {
        val announcement = PushMessage.from(mapOf("kind" to "BROADCAST"))
        assertNull(PushRouter.routeFor(announcement))
    }

    @Test
    fun the_thirty_minute_scheduled_reminder_opens_the_upcoming_list() {
        // US-6A.15 / D2' §SCR-DA-018: "30-min reminder push deep-links here". It carries no link to
        // follow — `DeepLinks` in Notification.Api mints four URIs and none names a scheduled ride —
        // so the routing is on the type name D5' §14.4 and `NotificationCatalogue` both fix.
        val reminder = PushMessage.from(
            mapOf("kind" to "SCHEDULED_REMINDER", "notificationId" to "01JNOTIF000000000000000000"),
        )

        assertEquals(DriverRoute.ScheduledRides, PushRouter.routeFor(reminder))
        assertNull(PushRouter.resolve("mageride://scheduled"), "there is no such host, and none is invented")
    }

    @Test
    fun the_c072_screens_are_registered_destinations() {
        // SCR-DA-018/019/020 are pushed screens — each wireframe draws a `‹` app bar — and the
        // shell's table had none of them (Δ C072). A screen registered nowhere is a tap that does
        // nothing, which is the same failure `MenuDestinationTest` guards the drawer against.
        listOf(DriverRoute.Jobs, DriverRoute.ScheduledRides, DriverRoute.DriverLevel, DriverRoute.Earnings)
            .forEach { assertTrue(it in DriverRoute.Static, "${it.path} is not registered") }

        assertTrue(!isTabRoute(DriverRoute.ScheduledRides.path), "a pushed screen shows no bottom bar")
        assertTrue(!isTabRoute(DriverRoute.DriverLevel.path))
        assertTrue(!isTabRoute(DriverRoute.Earnings.path))
        assertTrue(isTabRoute(DriverRoute.Jobs.path), "the Job Board IS tab 2")
    }

    @Test
    fun the_four_drawer_destinations_c074_owns_are_registered_and_are_not_tabs() {
        // SCR-DA-027/028/029/030. C070 added three of these routes so no drawer row was dead and
        // registered them against the standing placeholder; C074 replaced the placeholders. Every
        // one draws a `‹` app bar in the wireframe, so none of them shows the bottom bar.
        listOf(
            DriverRoute.TrackerPairing,
            DriverRoute.Sharing,
            DriverRoute.Profile,
            DriverRoute.RideHistory,
        ).forEach {
            assertTrue(it in DriverRoute.Static, "${it.path} is not registered")
            assertTrue(!isTabRoute(it.path), "${it.path} must not show the bottom bar")
        }
    }

    @Test
    fun the_call_and_the_alarm_are_parameterised_takeovers_and_never_tabs() {
        // SCR-DA-031 and SCR-DA-032 (Δ C075). Both are *about* a ride, so both carry its id rather
        // than reading a process-wide holder; both are full-screen with no bottom bar, because a
        // driver on a call or raising an alarm has nowhere else to be.
        val call = DriverRoute.VoipCall("01JRIDE0000000000000000000")
        val sos = DriverRoute.Sos("01JRIDE0000000000000000000")

        assertEquals("call/01JRIDE0000000000000000000", call.path)
        assertEquals("sos/01JRIDE0000000000000000000", sos.path)
        assertTrue(!isTabRoute(call.path))
        assertTrue(!isTabRoute(sos.path))

        // Absent from `Static` on purpose — the NavHost registers their PATTERNs, exactly as it
        // does for `ActiveRide`.
        assertTrue(DriverRoute.Static.none { it.path == call.path || it.path == sos.path })
        assertEquals("call/{rideId}", DriverRoute.VoipCall.PATTERN)
        assertEquals("sos/{rideId}", DriverRoute.Sos.PATTERN)
    }

    @Test
    fun the_two_menu_destinations_c075_owns_are_registered_and_are_not_tabs() {
        // SCR-DA-033 and SCR-DA-034 — the last two rows of AL-31's drawer to get a real screen.
        listOf(DriverRoute.Support, DriverRoute.Notifications).forEach {
            assertTrue(it in DriverRoute.Static, "${it.path} is not registered")
            assertTrue(!isTabRoute(it.path), "${it.path} must not show the bottom bar")
        }

        // `mageride://` has no host for either, and none is invented: the alerts list is reached
        // from the Menu, and support from the Menu or from a ticket a driver already raised.
        assertNull(PushRouter.resolve("mageride://support"))
        assertNull(PushRouter.resolve("mageride://notifications"))
    }

    @Test
    fun the_wallet_deep_link_lands_on_the_tab_and_not_on_one_of_its_four_children() {
        // `mageride://wallet` is one of the four URIs `DeepLinks` mints and it names the *screen
        // group*, not a screen in it. LOW_BALANCE and TOP_UP_REQUIRED both carry it, and both mean
        // "look at your wallet" rather than "top up right now" — a link that opened SCR-DA-022
        // would put a driver on a payment form they did not ask for (Δ C073).
        assertEquals(DriverRoute.Wallet, PushRouter.resolve("mageride://wallet"))
        // The host names the group and a trailing segment is not a second host, so even a link
        // that looked like one still lands on SCR-DA-021.
        assertEquals(DriverRoute.Wallet, PushRouter.resolve("mageride://wallet/top-up"))
    }
}
