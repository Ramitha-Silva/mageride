package lk.mageride.driver.wallet

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.domain.wallet.WalletAlert
import lk.mageride.shared.domain.wallet.WalletRules

/**
 * SCR-DA-021's state.
 *
 * @property loading The three reads are in flight.
 * @property standing The balance, today's fee and the rate table.
 * @property threshold The **driver's** low-balance line — see [WalletPreferences].
 * @property error Resolved copy for the last failure.
 */
internal data class WalletState(
    val loading: Boolean = true,
    val standing: WalletFeeStanding = WalletFeeStanding(),
    val threshold: Money = WalletRules.DEFAULT_LOW_BALANCE_THRESHOLD,
    @param:StringRes val error: Int? = null,
) {

    /** The read-only figure the screen leads with (US-9.7). `null` until wallet-svc answers. */
    val balance: Money? get() = standing.standing?.balance

    /** What may actually be spent — the balance net of accrued penalty debt (D-05). */
    val available: Money? get() = standing.available

    /** US-9.9 / D5' §9.4, evaluated against the driver's own threshold rather than a baked one. */
    val alert: WalletAlert
        get() = standing.standing?.let { WalletRules.alertFor(it, threshold) } ?: WalletAlert.None

    /**
     * D2' §SCR-DA-021's *"below one day's fee → Top Up Required"*.
     *
     * **A different question from [alert], and both are in the spec.** D5' §9.4 draws the
     * *"Top Up Required"* banner at a **negative** balance and `:shared`'s `WalletRules` implements
     * that; D2' draws it at **below one day's fee**, which is US-9.1's real consequence — a driver
     * who cannot cover the rate has their next request refused with `402 insufficient-wallet`
     * fifteen seconds after an offer arrives. Neither is wrong and neither subsumes the other, so
     * the screen ranks them: overdrawn is the harder state and wins, this is the next, and the
     * low-balance nudge is the softest.
     *
     * `false` once the day's fee is paid: trips 2..N are free after the deduction (US-9.4), so
     * there is nothing left today for the balance to be short of.
     */
    val belowDayFee: Boolean
        get() {
            if (standing.feePaid) return false
            val rate = standing.dailyRate ?: return false
            val available = available ?: return false
            return rate > Money.ZERO && available < rate
        }
}

/**
 * **SCR-DA-021 · wallet & daily fee** (US-9.7, US-9.1, US-9.9).
 *
 * Three reads in one pass — the balance, today's fee row and the seven-tier rate table — each
 * best-effort, because a driver whose fee read failed still needs to see their money.
 *
 * **Nothing here computes a balance.** The ledger is the truth and the server holds it (D-09); this
 * class holds the server's figure and asks `:shared`'s rules what to say about it. The one number
 * that is genuinely the device's is the low-balance threshold, and [WalletPreferences] explains at
 * length why.
 *
 * The screen is re-read on [refresh] rather than subscribed to: D2' marks the balance as
 * event-updated, `LiveHub` has no wallet event in its contract, and there is no hub client in
 * `:shared` yet (the same gap SCR-DA-015 polls around). A read on open and after every action that
 * moves money is what keeps this screen honest until one lands.
 */
internal class WalletViewModel(
    private val identity: DriverIdentity,
    private val wallet: WalletRepository,
    private val preferences: WalletPreferences,
) : ViewModel() {

    private val mutableState = MutableStateFlow(WalletState(threshold = preferences.lowBalanceThreshold))

    val state: StateFlow<WalletState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** Re-reads the balance, the fee and the rate table. */
    fun refresh() {
        val driverId = identity.driverId ?: return
        mutableState.update { it.copy(loading = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val standing = wallet.standing(driverId)
            mutableState.update { it.copy(loading = false, standing = standing) }
        }
    }

    /**
     * Moves the driver's low-balance line (US-9.9's *"driver-set threshold"*).
     *
     * Persisted immediately and applied to the state in the same breath — there is no round trip
     * to wait for, because there is no route to make one to.
     */
    fun setThreshold(amountMinor: Long) {
        preferences.lowBalanceThresholdMinor = amountMinor
        mutableState.update { it.copy(threshold = preferences.lowBalanceThreshold) }
    }

    /** Puts the threshold back to D5' §9.4's Rs 200. */
    fun clearThreshold() {
        preferences.lowBalanceThresholdMinor = null
        mutableState.update { it.copy(threshold = preferences.lowBalanceThreshold) }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the driver raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }
}
