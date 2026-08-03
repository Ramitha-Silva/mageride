package lk.mageride.driver.wallet

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri

/**
 * Leaving the app to pay — OnePay's hosted page, or a bank app on a LankaQR deep link (AL-15).
 *
 * A seam for the same reason [StatementExporter] is: an `Intent` needs a `Context`, and a view
 * model that held one could not be run on this host. The rule about *which* URL to open is
 * `:shared`'s (`TopupRules.actionFor`, which reuses the fare side's `PaymentMethods.lankaQrAction`
 * rather than restating AL-15 twice); all this interface decides is whether the handset could.
 *
 * ### Why "try, then fall back" rather than "ask, then open"
 *
 * AL-15 makes the QR a fallback *for a handset no bank app can open the link on*, so the natural
 * shape is to query the package manager first. On `targetSdk` 30+ that query answers nothing
 * useful: package-visibility filtering hides every app this one has not declared a `<queries>`
 * entry for, and a LankaQR *"Pay"* link's scheme is the issuing **bank's**, not a scheme this
 * platform mints — so there is no finite list to declare. **Launching is never filtered**, only
 * asking is, which is why [open] answers whether the launch happened and the screen falls back on
 * `false`. The alternative is a QR shown to a driver whose bank app was installed all along.
 */
internal interface PaymentHandoff {

    /**
     * Opens [url] outside the app.
     *
     * @return `false` when nothing on the handset handles it — the AL-15 fallback condition, and
     *   for OnePay a plain failure the sheet reports.
     */
    fun open(url: String): Boolean
}

/** [PaymentHandoff] over `ACTION_VIEW`. */
internal class AndroidPaymentHandoff(context: Context) : PaymentHandoff {

    private val appContext = context.applicationContext

    override fun open(url: String): Boolean = try {
        // NEW_TASK because this is launched from the application context, not an Activity: the
        // seam exists precisely so the caller need not hold one.
        appContext.startActivity(
            Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        )
        true
    } catch (_: ActivityNotFoundException) {
        false
    }
}
