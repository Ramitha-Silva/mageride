package lk.mageride.driver.wallet

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import lk.mageride.shared.domain.wallet.WalletRules

/**
 * The **driver-set** low-balance threshold, and why it lives on the handset.
 *
 * D2' §SCR-DA-021 draws the warning at *"balance ≤ driver-set threshold (default Rs 200)"* and the
 * C073 deliverable asks for the setting. D5' §9.4 fixes the same Rs 200 and calls it
 * **admin-configurable** — and **no route on the platform stores a per-driver figure**: `iam.yaml`'s
 * profile carries no such field, wallet-svc has no preferences surface, and the only threshold the
 * server knows is the one it evaluates its own `LOW_BALANCE` push against.
 *
 * So the two are kept apart rather than reconciled. The **push** is the admin's threshold and this
 * app never sees it; the **on-screen nudge** is the driver's, stored here, defaulting to
 * [WalletRules.DEFAULT_LOW_BALANCE_THRESHOLD]. A driver who works a Rs 300/day van and wants to be
 * warned at Rs 600 gets that on their own screen, which is what the wireframe promises, and nothing
 * about it claims to have changed a server-side rule. Recorded as a spec gap in the C073 handoff.
 *
 * Local for the same reason `ActiveVehicleStore` and `JourneyPreferences` are: there is no
 * operation to call, and inventing a synthetic one would be worse than being honest about the scope.
 */
internal interface WalletPreferences {

    /**
     * Minor units. `null` means the driver has never set one and D5' §9.4's default applies.
     *
     * Nullable rather than pre-seeded with 20,000 so *"never changed it"* stays distinguishable
     * from *"deliberately chose Rs 200"* — the day the platform gains a per-driver setting, the
     * first is what should be migrated from the server and the second is what should not.
     */
    var lowBalanceThresholdMinor: Long?
}

/** The threshold as it will actually be applied — the driver's figure, or D5' §9.4's Rs 200. */
internal val WalletPreferences.lowBalanceThreshold
    get() = lowBalanceThresholdMinor
        ?.let { lk.mageride.shared.data.models.Money.ofMinor(it) }
        ?: WalletRules.DEFAULT_LOW_BALANCE_THRESHOLD

/** [WalletPreferences] over a private `SharedPreferences` file. Nothing here is a secret. */
internal class AndroidWalletPreferences(context: Context) : WalletPreferences {

    private val store: SharedPreferences =
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    override var lowBalanceThresholdMinor: Long?
        get() = store.getLong(KEY_THRESHOLD, UNSET).takeIf { it != UNSET }
        set(value) = store.edit {
            if (value == null) remove(KEY_THRESHOLD) else putLong(KEY_THRESHOLD, value)
        }

    private companion object {
        const val FILE = "driver_wallet"
        const val KEY_THRESHOLD = "low_balance_threshold_minor"

        /** `getLong` has no "absent" answer, so one value stands for it. A threshold is never −1. */
        const val UNSET = -1L
    }
}
