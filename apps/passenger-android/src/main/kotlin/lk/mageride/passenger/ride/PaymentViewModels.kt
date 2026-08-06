package lk.mageride.passenger.ride

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * SCR-PA-016's state.
 *
 * @property methods Which rails this ride can use — [PaymentRails.RIDE], plus COD for a parcel.
 * @property walletBalanceMinor The passenger's spendable balance. `null` while it is being read.
 * @property amountMinor The fare. **The only number on this screen** — there is no surcharge line,
 *   because AL-57 retired the rail that had one.
 * @property chosen What Confirm will send.
 */
internal data class PaymentMethodState(
    val methods: List<PaymentMethod> = PaymentRails.RIDE,
    val walletBalanceMinor: Long? = null,
    val amountMinor: Long? = null,
    val chosen: PaymentMethod = PaymentMethod.CASH,
    val confirming: Boolean = false,
    val confirmed: PaymentMethod? = null,
    @param:StringRes val error: Int? = null,
) {

    /**
     * Whether the wallet can cover this fare.
     *
     * `false` turns the row's action into *"Top up"* rather than disabling it — a passenger who is
     * Rs 40 short should be one tap from fixing that, not told no. `fare.yaml` answers
     * `402 insufficient-wallet` if they try anyway, **with cash and driver-QR still offered and
     * never a silent fallback to cash**.
     */
    val walletCovers: Boolean
        get() = walletBalanceMinor != null && amountMinor != null && walletBalanceMinor >= amountMinor

    /** Whether the wallet row should offer a top-up instead of a selection. */
    val walletShort: Boolean get() = walletBalanceMinor != null && !walletCovers
}

/**
 * SCR-PA-016 — how this ride gets paid.
 *
 * **Three rails, and the two that are missing are the story.** AL-57 removed `onepay` (one merchant
 * account per merchant, so a card fare could only land in MageRide's own account — card acceptance
 * moved to the wallet **top-up**, where MageRide legitimately is the payee) and AL-59 removed the
 * platform-merchant `lankaqr` (it collected into the platform account while crediting the driver a
 * read-model row). What survives is cash, the passenger wallet, and the driver's own QR.
 *
 * **There is therefore no surcharge anywhere on this screen.** The +5 % existed to recover OnePay's
 * ~3 % on the ride and died with it; [PaymentMethodState] has no surcharge field to render.
 *
 * The wireframe still draws Cash / LankaQR / OnePay +5 %, which predates the 2026-08-01 change set;
 * the prompt's own fence and Definition of Done name AL-57/AL-59 explicitly. Recorded in the C080
 * handoff as a wireframe that needs a micro-change-set.
 */
internal class PaymentMethodViewModel(
    private val rideId: String,
    private val rides: RideRepository,
    private val sessions: AuthSessionManager,
) : ViewModel() {

    private val mutableState = MutableStateFlow(PaymentMethodState())

    val state: StateFlow<PaymentMethodState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch { load() }
    }

    fun choose(method: PaymentMethod) {
        mutableState.update { it.copy(chosen = method, error = null) }
    }

    /**
     * Confirm.
     *
     * This screen does **not** call `POST /v1/fare/pay` — SCR-PA-017 does, because the wallet rail
     * settles on the spot and the driver-QR rail needs the QR image the initiation returns. What
     * Confirm does is decide which screen comes next.
     */
    fun confirm() {
        mutableState.update { it.copy(confirmed = it.chosen) }
    }

    fun onConfirmConsumed() {
        mutableState.update { it.copy(confirmed = null) }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val ride = rides.ride(rideId)
            mutableState.update {
                it.copy(
                    methods = if (ride.kind == RideKind.PACKAGE) PaymentRails.PACKAGE else PaymentRails.RIDE,
                    amountMinor = ride.fare?.amountMinor,
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(error = RideErrors.messageFor(cause)) }
        }

        // The balance is read separately and is allowed to fail on its own: a wallet-svc outage
        // should cost the passenger the wallet row, not the whole payment screen.
        val userId = sessions.state.value.userIdOrNull() ?: return
        try {
            mutableState.update { it.copy(walletBalanceMinor = rides.wallet(userId).availableMinor) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // No balance shown. The row offers a top-up, which is the safe direction.
        }
    }
}

/**
 * SCR-PA-017's state.
 *
 * @property qrImageUrl The **driver's own** bank QR, as a signed URL (AL-59). Shown as a fallback
 *   for a handset whose camera cannot read the printed one.
 * @property claimed AL-47's *"I've paid"* has been sent; the screen is waiting for the driver.
 * @property secondsWaiting How long since the claim. Past five minutes the screen offers support.
 */
internal data class PayFareState(
    val amountMinor: Long? = null,
    val method: PaymentMethod = PaymentMethod.SCAN_DRIVER_QR,
    val paymentId: String? = null,
    val paymentState: PaymentState? = null,
    val qrImageUrl: String? = null,
    val scanning: Boolean = false,
    val busy: Boolean = false,
    val claimed: Boolean = false,
    val secondsWaiting: Int = 0,
    @param:StringRes val error: Int? = null,
) {
    /**
     * Whether the screen can show Confirmed ✓.
     *
     * `isTerminal` and not a hand-written list: `Disputed` and `Refunded` are terminal too and are
     * equally *"nothing more for this screen to do"*. What must **not** be in here is
     * `QrClaimedByPassenger` — that is the wait, and treating it as settled would tell a passenger
     * their driver had confirmed when the driver has not been asked yet.
     */
    val confirmed: Boolean get() = paymentState?.isTerminal == true

    /** Whether *"Get help"* should be offered — the claim has gone unanswered past the nudge. */
    val offerSupport: Boolean get() = claimed && !confirmed && secondsWaiting >= UNCONFIRMED_SECONDS

    /** `88s`, as the wireframe writes it. */
    val waiting: String get() = "${secondsWaiting}s"

    internal companion object {
        /** AL-47 re-pushes the driver at +5 min; past that the passenger is offered support. */
        const val UNCONFIRMED_SECONDS = 300
    }
}

/**
 * SCR-PA-017 — paying, and the attestation that follows.
 *
 * **The app renders no MageRide QR.** AL-22: the passenger *scans the driver's* printed or
 * on-screen LankaQR, so the money moves bank to bank and never passes through the platform. That
 * is also why settlement is an attestation rather than a webhook — **no callback ever reaches
 * fare-svc** (AL-47), so the only oracle is the two parties: the passenger claims, the driver
 * confirms, and `DriverConfirmedQR` is terminal.
 *
 * **A claim without a confirm is not a failure, it is a wait.** The driver is re-pushed at five
 * minutes; past that the screen offers Support, which routes to the Finance dispute queue. No money
 * moves either way — there is nothing for the platform to reverse.
 */
internal class PayFareViewModel(
    private val rideId: String,
    private val rides: RideRepository,
    private val now: () -> Timestamp = { Clock.System.now() },
    /**
     * How often the wait for the driver's confirm re-reads the payment.
     *
     * Injected so a test can assert the attestation pair without sleeping through it — the same
     * reason `PassengerLiveMap` takes its backoff. Five seconds in production: often enough to feel
     * immediate, slow enough not to be a request a second for five minutes.
     */
    private val pollInterval: Duration = CONFIRM_POLL,
) : ViewModel() {

    private val mutableState = MutableStateFlow(PayFareState())

    val state: StateFlow<PayFareState> = mutableState.asStateFlow()

    private var claimedAt: Timestamp? = null

    init {
        viewModelScope.launch { load() }
    }

    /** The chosen rail arrives from SCR-PA-016. */
    fun setMethod(method: PaymentMethod) {
        mutableState.update { it.copy(method = method) }
        viewModelScope.launch { initiate(method) }
    }

    fun openScanner() {
        mutableState.update { it.copy(scanning = true, error = null) }
    }

    fun closeScanner() {
        mutableState.update { it.copy(scanning = false) }
    }

    /**
     * The camera decoded the driver's QR.
     *
     * The payload goes to `POST /v1/fare/pay/scan-driver-qr` **as read** — it is the driver's
     * merchant string and this app does not interpret it. Recording the scan is what lets a later
     * dispute say which QR was presented.
     */
    @Suppress("TooGenericExceptionCaught")
    fun onQrScanned(payload: String) {
        mutableState.update { it.copy(scanning = false, busy = true, error = null) }
        viewModelScope.launch {
            try {
                val status = rides.payByScanningDriverQr(rideId, payload)
                mutableState.update {
                    it.copy(busy = false, paymentId = status.paymentId, paymentState = status.state)
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(busy = false, error = RideErrors.messageFor(cause)) }
            }
        }
    }

    /**
     * AL-47's *"I've paid"*.
     *
     * @param receiptArtifactId The bank app's receipt screenshot, when the passenger attached one.
     *   **This is what a dispute is adjudicated on** — there is no gateway record to fall back to.
     */
    @Suppress("TooGenericExceptionCaught")
    fun claimPaid(receiptArtifactId: String? = null) {
        if (mutableState.value.busy) return
        mutableState.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                val status = rides.claimDriverQrPaid(rideId, receiptArtifactId)
                claimedAt = now()
                mutableState.update {
                    it.copy(busy = false, claimed = true, paymentId = status.paymentId, paymentState = status.state)
                }
                awaitDriverConfirmation()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(busy = false, error = RideErrors.messageFor(cause)) }
            }
        }
    }

    /** US-8.15 — a rail that will not settle becomes cash, without losing the payment's history. */
    @Suppress("TooGenericExceptionCaught")
    fun switchToCash() {
        val paymentId = mutableState.value.paymentId ?: return
        mutableState.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                val status = rides.fallbackToCash(paymentId)
                mutableState.update { it.copy(busy = false, paymentState = status.state) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(busy = false, error = RideErrors.messageFor(cause)) }
            }
        }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    // ------------------------------------------------------------------------------------------

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val ride = rides.ride(rideId)
            mutableState.update { it.copy(amountMinor = ride.fare?.amountMinor) }
            initiate(mutableState.value.method)
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(error = RideErrors.messageFor(cause)) }
        }
    }

    /**
     * `POST /v1/fare/pay`.
     *
     * A **wallet** payment is `Succeeded` the moment this returns — one balanced journal entry,
     * no gateway, no `Pending` (AL-57) — so the screen goes straight to the receipt. A
     * **driver-QR** payment comes back with the driver's QR image URL and nothing settled.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun initiate(method: PaymentMethod) {
        mutableState.update { it.copy(busy = true, error = null) }
        try {
            val initiation = rides.pay(rideId, method)
            mutableState.update {
                it.copy(
                    busy = false,
                    paymentId = initiation.paymentId,
                    paymentState = initiation.state,
                    qrImageUrl = initiation.driverQr?.qrImageUrl,
                    amountMinor = initiation.amountMinor,
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(busy = false, error = RideErrors.messageFor(cause)) }
        }
    }

    /**
     * The wait between *"I've paid"* and Confirmed ✓.
     *
     * A poll rather than a socket subscription: `DriverConfirmedQR` is a **payment** transition and
     * the passenger's SignalR groups are the ride's, so there is no event to subscribe to. The
     * counter is what turns a five-minute silence into an offer of help rather than a spinner.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun awaitDriverConfirmation() {
        val paymentId = mutableState.value.paymentId ?: return
        while (!mutableState.value.confirmed) {
            val since = claimedAt?.let { (now() - it).inWholeSeconds.toInt() } ?: 0
            mutableState.update { it.copy(secondsWaiting = since) }
            delay(pollInterval)
            try {
                val status = rides.paymentStatus(paymentId)
                mutableState.update { it.copy(paymentState = status.state) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // Keep waiting. The driver confirming is not something this app can hurry.
            }
        }
    }

    private companion object {
        /** The production cadence. AL-47 re-pushes the driver at +5 min; this is the client's poll. */
        val CONFIRM_POLL = 5.seconds
    }
}

/** The signed-in user, or `null` — the wallet read needs one and a signed-out screen has none. */
private fun SessionState.userIdOrNull(): String? = (this as? SessionState.SignedIn)?.userId

/** The ride's fare, for the screens that only need the number. */
internal fun RideDetail.amountMinorOrNull(): Long? = fare?.amountMinor
