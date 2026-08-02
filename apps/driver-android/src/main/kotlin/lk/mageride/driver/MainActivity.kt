package lk.mageride.driver

import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import lk.mageride.driver.push.PushRouter
import lk.mageride.driver.shell.DriverShell
import lk.mageride.driver.ui.theme.MageRideTheme
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

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // A cold start from a notification tap: the deep link is on the launching intent and
        // nowhere else. Offering it to the router before `setContent` means the shell's collector
        // finds it already replayed rather than missing it by a frame.
        routeFromIntent(intent)

        setContent {
            MageRideTheme {
                DriverShell(onOpenUrl = ::openExternally)
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
     * Opens the Play Store link from D-31's `426` payload.
     *
     * A null or unopenable URL is not an error worth surfacing: the gate itself already tells the
     * driver what is wrong, and a missing store link is the platform's misconfiguration rather
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
