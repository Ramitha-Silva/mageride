package lk.mageride.passenger.onboarding

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import androidx.core.content.ContextCompat

/**
 * The one runtime permission this app asks for.
 *
 * **Foreground location, and nothing else.** The driver app asks for background location, a
 * foreground-service type, a wake lock and notifications because it publishes a position stream
 * through a shift; a passenger publishes nothing (D3' §3.3). What the grant is for is the R-06
 * geocell anchor, MAP-02's accuracy circle and a pickup that defaults to where the passenger is —
 * all of which happen with the app open.
 *
 * `POST_NOTIFICATIONS` is declared in the manifest but is **not** asked for here: SCR-PA-005 is
 * about location and says so, and Android 13+ prompts for notifications the first time a channel
 * posts. Asking for two things behind one rationale is how a passenger denies both.
 */
internal class LocationPermission(private val context: Context) {

    /**
     * Whether either precision has been granted.
     *
     * COARSE counts: Android 12+ lets a user grant approximate location from the system dialog,
     * and a ~3 km live map works perfectly well at that precision. Treating it as "denied" would
     * put the rationale in front of a passenger who has already said yes.
     */
    fun isGranted(): Boolean = REQUESTED.any {
        ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED
    }

    /**
     * The app's own settings page — SCR-PA-005's *"Open Settings"* on a denial.
     *
     * Needed because Android stops showing the system dialog after two refusals: from then on the
     * only way to grant is Settings, and a CTA that silently did nothing would be worse than no
     * CTA at all.
     */
    fun settingsIntent(): Intent = Intent(
        Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
        Uri.fromParts("package", context.packageName, null),
    ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    internal companion object {

        /**
         * What the launcher asks for.
         *
         * Both precisions in one request: asking for FINE alone still lets the user pick
         * approximate, and declaring COARSE beside it is what makes that choice legible in the
         * system dialog rather than a silent downgrade.
         */
        val REQUESTED: Array<String> = arrayOf(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_COARSE_LOCATION,
        )
    }
}
