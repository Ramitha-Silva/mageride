package lk.mageride.driver.onboarding

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.annotation.StringRes
import androidx.core.content.ContextCompat
import androidx.core.content.getSystemService
import lk.mageride.driver.R

/**
 * The four rows of SCR-DA-007, and what each of them actually asks the OS for.
 *
 * D2' §SCR-DA-007 `[DELTA:PLATFORM]` names the Android set exactly: *"ACCESS_BACKGROUND_LOCATION +
 * FOREGROUND_SERVICE_LOCATION + POST_NOTIFICATIONS + battery-optimization intent"*, plus the
 * overlay row the wireframe draws. They are **not** all the same kind of thing, which is why this
 * is an enum with a [kind] rather than four permission strings:
 *
 * * two are runtime permissions granted by a dialog,
 * * one is a system exemption granted by its own settings screen,
 * * one is a special app access granted by another.
 *
 * @property title The row's label.
 * @property rationale The one line under it — *why*, in the driver's language, before the dialog.
 * @property kind How this one is asked for.
 * @property required Whether the driver can reach the dashboard without it. Only the two location
 *   and notification rows gate anything at all, and even they gate **going online**, not Home:
 *   AL-27 puts nothing between Profile Setup and the dashboard.
 */
internal enum class DriverPermission(
    @param:StringRes val title: Int,
    @param:StringRes val rationale: Int,
    val kind: PermissionKind,
    val required: Boolean,
) {

    /**
     * Foreground location. Asked first and on its own: from Android 11 the background grant is
     * only offered *after* this one exists, and asking for both together silently denies both.
     */
    LOCATION(
        title = R.string.permission_location_title,
        rationale = R.string.permission_location_rationale,
        kind = PermissionKind.RUNTIME,
        required = true,
    ),

    /**
     * `ACCESS_BACKGROUND_LOCATION` — publishing with the app behind the lock screen, which is
     * where a mounted handset spends a ride (D6' §3).
     */
    BACKGROUND_LOCATION(
        title = R.string.permission_background_location_title,
        rationale = R.string.permission_background_location_rationale,
        kind = PermissionKind.RUNTIME,
        required = true,
    ),

    /** `POST_NOTIFICATIONS` — E-01's offers live 15 seconds; an unnotifiable driver is undispatchable. */
    NOTIFICATIONS(
        title = R.string.permission_notifications_title,
        rationale = R.string.permission_notifications_rationale,
        kind = PermissionKind.RUNTIME,
        required = true,
    ),

    /**
     * Battery-optimisation exemption. Doze otherwise stretches the D5' §5.2 cadence into minutes,
     * and a vehicle publishing once a minute is a vehicle the map has lost.
     */
    BATTERY(
        title = R.string.permission_battery_title,
        rationale = R.string.permission_battery_rationale,
        kind = PermissionKind.SETTINGS,
        required = false,
    ),

    /** Display over other apps — the offer takeover while the driver is in another app. */
    OVERLAY(
        title = R.string.permission_overlay_title,
        rationale = R.string.permission_overlay_rationale,
        kind = PermissionKind.SETTINGS,
        required = false,
    ),
}

/** How a [DriverPermission] is asked for. */
internal enum class PermissionKind {

    /** A runtime dialog — `ActivityResultContracts.RequestPermission`. */
    RUNTIME,

    /** A settings screen the app can only open; the system decides, and may not come back. */
    SETTINGS,
}

/**
 * Reads and asks for the SCR-DA-007 set.
 *
 * A class rather than free functions so the screen can be driven by a fake in a unit test: every
 * member below is `Context`-bound Android API whose local-unit-test stub returns a default, and a
 * screen that believed those defaults would report every permission granted.
 */
internal open class DriverPermissions(private val context: Context) {

    /** Whether [permission] is currently held. */
    open fun isGranted(permission: DriverPermission): Boolean = when (permission) {
        DriverPermission.LOCATION -> hasRuntime(Manifest.permission.ACCESS_FINE_LOCATION)
        DriverPermission.BACKGROUND_LOCATION -> hasBackgroundLocation()
        DriverPermission.NOTIFICATIONS -> hasNotifications()
        DriverPermission.BATTERY -> isIgnoringBatteryOptimisations()
        DriverPermission.OVERLAY -> Settings.canDrawOverlays(context)
    }

    /**
     * The manifest permission strings to request for a [PermissionKind.RUNTIME] row, or empty when
     * this API level grants it without asking.
     *
     * `POST_NOTIFICATIONS` did not exist before API 33 and `ACCESS_BACKGROUND_LOCATION` was implied
     * by the foreground grant before API 29 — on the URD NFR-22 floor (API 26) both are already
     * held, and requesting a permission the platform does not know is an immediate denial.
     */
    open fun runtimeRequestsFor(permission: DriverPermission): Array<String> = when (permission) {
        DriverPermission.LOCATION -> arrayOf(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_COARSE_LOCATION,
        )

        DriverPermission.BACKGROUND_LOCATION ->
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                arrayOf(Manifest.permission.ACCESS_BACKGROUND_LOCATION)
            } else {
                emptyArray()
            }

        DriverPermission.NOTIFICATIONS ->
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                arrayOf(Manifest.permission.POST_NOTIFICATIONS)
            } else {
                emptyArray()
            }

        else -> emptyArray()
    }

    /**
     * The settings screen for a [PermissionKind.SETTINGS] row, or for a runtime permission the
     * driver has denied permanently — D2's *"denied → Settings deep-link"*.
     */
    @SuppressLint("BatteryLife")
    open fun settingsIntent(permission: DriverPermission): Intent {
        val app = Uri.fromParts("package", context.packageName, null)
        return when (permission) {
            // Google Play restricts this action to apps whose core function needs the exemption.
            // A driver app that stops publishing GPS in Doze has stopped being a driver app, which
            // is the case D7'/D-30's release checklist makes; the lint is suppressed knowingly.
            DriverPermission.BATTERY ->
                Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, app)

            DriverPermission.OVERLAY ->
                Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, app)

            // Everything else goes to this app's own settings page rather than a global one: a
            // driver sent to the top of Settings has to find the app themselves.
            else -> Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, app)
        }
    }

    private fun hasRuntime(permission: String): Boolean =
        ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED

    private fun hasBackgroundLocation(): Boolean = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
        hasRuntime(Manifest.permission.ACCESS_BACKGROUND_LOCATION)
    } else {
        hasRuntime(Manifest.permission.ACCESS_FINE_LOCATION)
    }

    private fun hasNotifications(): Boolean = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        hasRuntime(Manifest.permission.POST_NOTIFICATIONS)
    } else {
        true
    }

    private fun isIgnoringBatteryOptimisations(): Boolean =
        context.getSystemService<PowerManager>()?.isIgnoringBatteryOptimizations(context.packageName) == true
}
