package lk.mageride.driver.onboarding

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * SCR-DA-007's row set, and the one thing about it that is not a preference.
 *
 * D2' §SCR-DA-007 `[DELTA:PLATFORM]` names the Android four exactly — background location,
 * foreground-service location, notifications and the battery intent — and the wireframe draws the
 * overlay row beside them. A row quietly dropped here is a driver who is never asked for the
 * permission the position plane cannot work without, and that failure only shows up on a ride.
 */
class DriverPermissionTest {

    @Test
    fun the_wireframes_rows_are_all_present_and_in_its_order() {
        assertEquals(
            listOf(
                DriverPermission.LOCATION,
                DriverPermission.BACKGROUND_LOCATION,
                DriverPermission.NOTIFICATIONS,
                DriverPermission.BATTERY,
                DriverPermission.OVERLAY,
            ),
            DriverPermission.entries,
            "the driver_android.html wireframe's 📍 🔔 🔋 ▢, with location split for Android 11+",
        )
    }

    @Test
    fun location_is_asked_for_before_background_location() {
        // From Android 11 the "Allow all the time" option is only offered once the foreground
        // grant exists, and asking for both in one dialog denies both silently. Order is the fix,
        // and the enum's order is what the screen iterates.
        assertTrue(
            DriverPermission.entries.indexOf(DriverPermission.LOCATION) <
                DriverPermission.entries.indexOf(DriverPermission.BACKGROUND_LOCATION),
        )
    }

    @Test
    fun the_two_system_exemptions_are_not_runtime_dialogs() {
        // Neither can be granted by `requestPermissions` at all — they are settings screens the
        // app can open and nothing more. Treating one as a runtime permission produces a request
        // that returns denied instantly and a switch that never turns on.
        assertEquals(PermissionKind.SETTINGS, DriverPermission.BATTERY.kind)
        assertEquals(PermissionKind.SETTINGS, DriverPermission.OVERLAY.kind)
        assertEquals(PermissionKind.RUNTIME, DriverPermission.LOCATION.kind)
        assertEquals(PermissionKind.RUNTIME, DriverPermission.BACKGROUND_LOCATION.kind)
        assertEquals(PermissionKind.RUNTIME, DriverPermission.NOTIFICATIONS.kind)
    }

    @Test
    fun every_row_carries_its_own_rationale() {
        // A runtime dialog is shown once and the driver decides in that moment; the line under the
        // row is the only explanation they get, and it has to be in their language.
        DriverPermission.entries.forEach { permission ->
            assertTrue(permission.title != 0, "${permission.name} has no title")
            assertTrue(permission.rationale != 0, "${permission.name} has no rationale")
            assertTrue(permission.rationale != permission.title, "${permission.name} reuses its title")
        }
    }

    @Test
    fun nothing_on_this_screen_blocks_the_dashboard() {
        // AL-27 puts nothing between Profile Setup and Home. What a refusal costs is going ONLINE,
        // which is the dashboard's gate (US-9.6) — a screen a driver cannot leave is uninstalled.
        assertTrue(
            DriverPermission.entries.none { it.kind == PermissionKind.SETTINGS && it.required },
            "a settings screen may not come back at all; requiring one would be a dead end",
        )
    }
}
