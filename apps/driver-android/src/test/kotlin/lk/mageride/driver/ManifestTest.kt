package lk.mageride.driver

import java.io.File
import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The manifest declarations the app's two background capabilities do not work without.
 *
 * None of this is testable at runtime on this host — a foreground service needs a device, and Doze
 * needs one that has been idle for an hour. What *is* checkable, and what actually breaks, is the
 * declaration: a `foregroundServiceType` that is missing throws
 * `MissingForegroundServiceTypeException` on API 34+ the first time a driver goes online, and an
 * undeclared `WAKE_LOCK` makes `newWakeLock().acquire()` throw `SecurityException` — both of them
 * the first time it matters, in production, on a ride.
 *
 * It also asserts the one thing that must NOT be here: `usesCleartextTraffic` is scoped to the
 * debug variant, so a release build physically cannot talk plain HTTP to anything (ADD §12.2).
 */
class ManifestTest {

    // Comments are stripped first. Half of what is asserted below is *also* discussed in a comment
    // in the same file, so a substring search over the raw text would pass on the explanation of a
    // declaration that had been deleted.
    private val main = declarations("src/main/AndroidManifest.xml")
    private val debug = declarations("src/debug/AndroidManifest.xml")

    @Test
    fun the_position_service_is_declared_as_a_location_foreground_service() {
        assertTrue(
            main.contains("""android:name=".location.PositionForegroundService""""),
            "the position service is not in the manifest",
        )
        // `location` is the type that keeps FusedLocation delivering with the screen off. Without
        // it the service starts and then silently receives nothing.
        assertTrue(
            main.contains("""android:foregroundServiceType="location""""),
            "the service has no foregroundServiceType",
        )
    }

    @Test
    fun every_permission_the_background_stream_needs_is_declared() {
        listOf(
            // D6' §3 / D5' §5 — the position plane.
            "android.permission.ACCESS_FINE_LOCATION",
            // Publishing with the app behind the lock screen, which is where a mounted handset
            // spends a ride.
            "android.permission.ACCESS_BACKGROUND_LOCATION",
            "android.permission.FOREGROUND_SERVICE",
            "android.permission.FOREGROUND_SERVICE_LOCATION",
            // The PARTIAL_WAKE_LOCK that stops Doze stretching the D5' §5.2 cadence into minutes.
            "android.permission.WAKE_LOCK",
            // E-01's offers live 15 seconds; a driver who cannot be notified cannot be dispatched.
            "android.permission.POST_NOTIFICATIONS",
            "android.permission.INTERNET",
        ).forEach { permission ->
            assertTrue(main.contains(permission), "$permission is not declared")
        }
    }

    @Test
    fun the_fcm_receiver_is_registered_for_messaging_events() {
        assertTrue(
            main.contains("""android:name=".push.DriverMessagingService""""),
            "the FCM service is not in the manifest",
        )
        assertTrue(
            main.contains("com.google.firebase.MESSAGING_EVENT"),
            "the FCM service has no MESSAGING_EVENT filter — a backgrounded app has no socket",
        )
    }

    @Test
    fun the_mageride_scheme_is_registered_so_a_notification_tap_opens_a_screen() {
        assertTrue(main.contains("""android:scheme="mageride""""), "the deep-link filter is missing")
    }

    @Test
    fun cleartext_traffic_is_debug_only() {
        assertFalse(
            main.contains("usesCleartextTraffic"),
            "a release build must not permit plain HTTP — production is HTTPS at HAProxy (ADD §12.2)",
        )
        assertTrue(debug.contains("""android:usesCleartextTraffic="true""""), "the dev gateway is plain HTTP")
    }

    /** The manifest with its XML comments removed. */
    private fun declarations(path: String): String =
        File(path).readText().replace(Regex("""<!--.*?-->""", RegexOption.DOT_MATCHES_ALL), "")
}
