package lk.mageride.passenger

import java.io.File
import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The manifest declarations this app does not work without — and the ones it must never grow.
 *
 * None of this is testable at runtime on this host. What *is* checkable, and what actually breaks,
 * is the declaration: an undeclared `ACCESS_FINE_LOCATION` makes the fused provider throw
 * `SecurityException` the first time a map is opened, and a missing `MESSAGING_EVENT` filter makes
 * every push silently vanish for a backgrounded app — which for P-02's 300-second location request
 * is the whole feature.
 *
 * The negative assertions matter more here than on the driver side. This app is a **passenger**
 * app: it publishes no position, so it must not carry background-location or foreground-service
 * permissions, and a later screen group that added one would be asking a passenger for a grant
 * nothing in the app uses. `usesCleartextTraffic` is scoped to the debug variant for the same
 * reason it is there — a release build must not be able to talk plain HTTP (ADD §12.2).
 */
class ManifestTest {

    // Comments are stripped first. Half of what is asserted below is *also* discussed in a comment
    // in the same file, so a substring search over the raw text would pass on the explanation of a
    // declaration that had been deleted.
    private val main = declarations("src/main/AndroidManifest.xml")
    private val debug = declarations("src/debug/AndroidManifest.xml")

    @Test
    fun every_permission_the_shell_needs_is_declared() {
        listOf(
            "android.permission.INTERNET",
            // The offline banner reads a VALIDATED network, which needs the callback.
            "android.permission.ACCESS_NETWORK_STATE",
            // R-06's geocell anchor and MAP-02's accuracy circle.
            "android.permission.ACCESS_FINE_LOCATION",
            "android.permission.ACCESS_COARSE_LOCATION",
            // Ride state, package handoff, and P-02's 300 s location request.
            "android.permission.POST_NOTIFICATIONS",
            // SCR-PA-017 scans the DRIVER's QR (AL-22); this app renders none of its own. Δ C080.
            "android.permission.CAMERA",
        ).forEach { permission ->
            assertTrue(main.contains(permission), "$permission is not declared")
        }
    }

    @Test
    fun the_passenger_app_asks_for_nothing_a_driver_app_needs() {
        // D3' §3.3: device position INGEST is MQTT and is the driver's; passenger realtime-OUT is
        // SignalR. A passenger publishes nothing, so there is no foreground service and no reason
        // to hold location with the screen off — and asking for background location is the single
        // most scrutinised grant on Play.
        listOf(
            "android.permission.ACCESS_BACKGROUND_LOCATION",
            "android.permission.FOREGROUND_SERVICE",
            "android.permission.FOREGROUND_SERVICE_LOCATION",
            "android.permission.WAKE_LOCK",
            // SCR-DA-007's overlay row is the driver's; a passenger has no 15-second offer to be
            // taken over by.
            "android.permission.SYSTEM_ALERT_WINDOW",
        ).forEach { permission ->
            assertFalse(main.contains(permission), "$permission has no justification in this app")
        }
    }

    @Test
    fun the_fcm_receiver_is_registered_for_messaging_events() {
        assertTrue(
            main.contains("""android:name=".push.PassengerMessagingService""""),
            "the FCM service is not in the manifest",
        )
        assertTrue(
            main.contains("com.google.firebase.MESSAGING_EVENT"),
            "the FCM service has no MESSAGING_EVENT filter — a backgrounded app has no socket",
        )
    }

    @Test
    fun the_mageride_scheme_is_registered_so_a_notification_tap_opens_a_screen() {
        // D6' I-23.3's `mageride://package/{rideId}` names SCR-PA-021 as its target, and
        // `mageride://ride/{id}` reaches SCR-PA-015. Without the filter both open the launcher.
        assertTrue(main.contains("""android:scheme="mageride""""), "the deep-link filter is missing")
    }

    @Test
    fun there_is_exactly_one_activity() {
        // D2' §0.1 — one Activity, one NavHost. A second would have its own back stack and its own
        // theme root, and the two would disagree the first time a push deep-linked into a screen
        // the other was already showing.
        assertEqualsCount(1, main, "<activity")
    }

    @Test
    fun cleartext_traffic_is_debug_only() {
        assertFalse(
            main.contains("usesCleartextTraffic"),
            "a release build must not permit plain HTTP — production is HTTPS at HAProxy (ADD §12.2)",
        )
        assertTrue(debug.contains("""android:usesCleartextTraffic="true""""), "the dev gateway is plain HTTP")
    }

    private fun assertEqualsCount(expected: Int, haystack: String, needle: String) {
        val found = haystack.windowed(needle.length).count { it == needle }
        assertTrue(found == expected, "expected $expected occurrences of '$needle', found $found")
    }

    /** The manifest with its XML comments removed. */
    private fun declarations(path: String): String =
        File(path).readText().replace(Regex("""<!--.*?-->""", RegexOption.DOT_MATCHES_ALL), "")
}
