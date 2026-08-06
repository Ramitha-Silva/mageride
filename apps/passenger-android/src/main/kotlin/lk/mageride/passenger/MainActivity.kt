package lk.mageride.passenger

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import lk.mageride.passenger.push.PushRouter
import lk.mageride.passenger.shell.PassengerLocale
import lk.mageride.passenger.shell.PassengerShell
import lk.mageride.passenger.ui.theme.MageRideTheme
import org.koin.android.ext.android.inject

/**
 * The app's single Activity.
 *
 * One Activity, one `NavHost` — D2' §0.1: *"a screen = a Compose `@Composable` route inside a
 * `NavHost`/`Scaffold`"*. Nothing else in this app may add an Activity; a second one would have
 * its own back stack and its own theme root, and the two would disagree the first time a push
 * deep-linked into a screen the other was already showing.
 */
internal class MainActivity : ComponentActivity() {

    private val pushes: PushRouter by inject()

    /**
     * Applies the language SCR-PA-002 chose (D-26, AL-26).
     *
     * `Resources` are resolved once per configuration and the first resolution happens before
     * `onCreate`, so this is the only hook early enough. Android's per-app locale API is API 33+
     * and the URD NFR-22 floor is 26; wrapping the base context works on every level and needs no
     * `appcompat`. A language change calls `recreate()`, which comes back through here.
     */
    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(PassengerLocale.wrap(newBase))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // A cold start from a notification tap: the deep link is on the launching intent and
        // nowhere else. Offering it to the router before `setContent` means the shell's collector
        // finds it already replayed rather than missing it by a frame.
        routeFromIntent(intent)

        setContent {
            MageRideTheme {
                PassengerShell(onOpenUrl = ::openExternally)
            }
        }
    }

    /** A tap while the app is already running: `singleTop` delivers the intent here, not to onCreate. */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        routeFromIntent(intent)
    }

    private fun routeFromIntent(intent: Intent?) {
        pushes.offer(intent?.data?.toString())
    }

    /**
     * Opens the Play Store link from D-31's `426` payload (SCR-PA-031).
     *
     * A null or unopenable URL is not an error worth surfacing: the gate itself already tells the
     * passenger what is wrong, and a missing store link is the platform's misconfiguration rather
     * than something they can act on.
     */
    private fun openExternally(url: String?) {
        val target = url?.takeIf(String::isNotBlank) ?: return
        try {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(target)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (_: ActivityNotFoundException) {
            // No browser and no store on the handset. Nothing useful to fall back to.
        }
    }
}
