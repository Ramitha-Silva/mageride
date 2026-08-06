package lk.mageride.passenger.subscription

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState
import lk.mageride.shared.domain.subscription.ModeBBilling
import lk.mageride.shared.domain.subscription.ModeBPaymentRules
import lk.mageride.shared.domain.subscription.ModeBPaymentStep

/**
 * SCR-PA-025a's state.
 *
 * [fare] and [step] are **derived, not stored**: a fare is the subscription's own
 * (`ModeBBilling.fareFor`) and a step is BR-23.10's answer for the chosen rail
 * (`ModeBPaymentRules.stepFor`). Copying either into a field would create a second value that can
 * disagree with the one it came from — which on a payment sheet means an amount or an account
 * number that is no longer the server's.
 *
 * @property method The chosen rail. Defaults to the wireframe's pre-selected LankaQR deep link.
 * @property payment The initiated payment, once **Confirm & pay** has been tapped. Its `payTo` is
 *   the only place the owner's collection details ever appear (AL-49).
 * @property slipName The attached screenshot's file name, for the row's caption. The bytes are
 *   held off the state — see [SubscriptionPayViewModel].
 */
internal data class SubscriptionPayState(
    val subscription: Subscription? = null,
    val method: SubscriptionPayMethod = SubscriptionRails.METHODS.first(),
    val payment: SubscriptionPayment? = null,
    val slipName: String? = null,
    val loading: Boolean = true,
    val submitting: Boolean = false,
    @param:StringRes val error: Int? = null,
) {

    /** What this subscriber owes. `null` on a Free subscription, which has no payment UI at all. */
    val fare: Money? get() = subscription?.let(ModeBBilling::fareFor)

    /** The amount actually being paid — the server's once there is a payment, the fare before. */
    val amount: Money? get() = payment?.money ?: fare

    /** What the sheet should do next. `null` until the payment exists, because `payTo` does not. */
    val step: ModeBPaymentStep? get() = payment?.let { ModeBPaymentRules.stepFor(method = method, payment = it) }

    /**
     * Whether **Confirm & pay** can fire.
     *
     * `online_transfer` is gated on the screenshot (US-23.4) — but only before initiation. After
     * it, the passenger has the bank details this screen just gave them and the attach is the next
     * step rather than a precondition.
     */
    val canConfirm: Boolean
        get() = subscription != null && payment == null && !submitting &&
            (!ModeBPaymentRules.requiresSlip(method) || slipName != null)

    /** The slip is still owed: the transfer was initiated and no screenshot has been accepted. */
    val awaitingSlip: Boolean
        get() = payment != null &&
            ModeBPaymentRules.requiresSlip(method) &&
            payment.status == SubscriptionPaymentStatus.INITIATED

    /** Nothing more to do here — the owner has it to verify, or it is already paid. */
    val settled: Boolean
        get() = payment?.status == SubscriptionPaymentStatus.PAID ||
            payment?.status == SubscriptionPaymentStatus.PENDING_VERIFICATION
}

/**
 * SCR-PA-025a — paying one Mode B month, to the fleet owner.
 *
 * **`payTo` is the OWNER's account and it only exists after the payment is initiated** (AL-49).
 * `POST /v1/mode-b/subscriptions/{id}/pay` is what mints it — a signed link to the owner's own
 * bank-app LankaQR image for the two QR rails, or bank/branch/account/holder for a transfer — and
 * it is served **only from a `verified` payout profile**, falling back to the last verified
 * snapshot rather than to an unverified edit. That is why this screen is two stages rather than
 * one: the chooser cannot show an account number it has not been given, and no client may invent
 * one. A fleet whose profile was never verified gets `409 payout-profile-not-verified`, which is
 * the only honest answer and has its own copy in [ModeBErrors].
 *
 * **OnePay is gone** (AL-59). The wireframe still draws it at +5 %, which would have routed a
 * passenger's payment into MageRide's merchant account instead of the fleet's — see
 * [SubscriptionRails]. Nothing on this screen carries a surcharge.
 *
 * **Two of the four rails settle on a human, not a webhook.** An `online_transfer` sits at
 * `pending_verification` until the owner confirms the slip (US-23.4), and `cash` is handed to a
 * collector and marked received by the owner in the portal (US-23.6) — `POST …/mark-cash` is
 * theirs and answers `403 not-owner` here. Neither can be completed from this handset, and the
 * screen says so rather than pretending to wait.
 */
internal class SubscriptionPayViewModel(
    private val subscriptionId: Ulid,
    private val subscriptions: SubscriptionRepository,
    private val sessions: AuthSessionManager,
    private val keys: IdempotencyKeyGenerator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SubscriptionPayState())
    private val mutableQrImage = MutableStateFlow<ByteArray?>(null)

    /** The attached screenshot. Held off the state because a `ByteArray` compares by identity. */
    private var slip: FileUpload? = null

    val state: StateFlow<SubscriptionPayState> = mutableState.asStateFlow()

    /**
     * The owner's bank-app LankaQR, as bytes, for the scan rail.
     *
     * A flow of its own rather than a field on [SubscriptionPayState]: a `ByteArray` has identity
     * equality, so a state holding one would compare unequal to itself and re-compose the whole
     * sheet on every unrelated update.
     */
    val qrImage: StateFlow<ByteArray?> = mutableQrImage.asStateFlow()

    init {
        load()
    }

    fun choose(method: SubscriptionPayMethod) {
        // A rail change before initiation only; afterwards the payment row is already typed with
        // the method the server accepted, and switching would silently orphan it.
        if (mutableState.value.payment != null) return
        mutableState.update { it.copy(method = method, error = null) }
    }

    /** The picker came back with a screenshot of the transfer slip (US-23.4). */
    fun attachSlip(fileName: String, bytes: ByteArray) {
        slip = FileUpload(fileName = fileName, bytes = bytes, contentType = IMAGE_CONTENT_TYPE)
        mutableState.update { it.copy(slipName = fileName, error = null) }
        // Attached after initiation — the passenger transferred the money using the details this
        // screen showed them, and the upload is the only thing still owed.
        if (mutableState.value.awaitingSlip) uploadSlip()
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * **Confirm & pay** — initiates the month and learns where the money goes.
     *
     * The idempotency key is minted once here, so a double tap is one payment row rather than two
     * months of debt (R-14). A slip attached before the tap is uploaded straight after, which is
     * the order a passenger who has already transferred the money will be in.
     */
    @Suppress("TooGenericExceptionCaught")
    fun confirm() {
        val current = mutableState.value
        if (!current.canConfirm) return

        mutableState.update { it.copy(submitting = true, error = null) }
        val key = keys.next()

        viewModelScope.launch {
            try {
                apply(subscriptions.pay(subscriptionId, current.method, key))
                if (mutableState.value.awaitingSlip && slip != null) uploadSlip() else loadQrImage()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(submitting = false, error = ModeBErrors.messageFor(cause)) }
            }
        }
    }

    // ------------------------------------------------------------------------------------------

    /**
     * Finds the subscription being paid.
     *
     * `GET /v1/mode-b/subscriptions/{passengerId}` is the only read that carries one, and it is a
     * list — there is no `GET …/subscriptions/{subscriptionId}` on this contract. So the screen
     * reads the passenger's own subscriptions and picks. Recorded in the C082 handoff.
     */
    @Suppress("TooGenericExceptionCaught")
    private fun load() {
        val passengerId = (sessions.state.value as? SessionState.SignedIn)?.userId
        if (passengerId == null) {
            mutableState.update { it.copy(loading = false) }
            return
        }

        viewModelScope.launch {
            try {
                val subscription = subscriptions.subscriptions(passengerId).items
                    .firstOrNull { it.subscriptionId == subscriptionId }
                mutableState.update { it.copy(loading = false, subscription = subscription) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(loading = false, error = ModeBErrors.messageFor(cause)) }
            }
        }
    }

    /** Uploads the slip and moves the payment to `pending_verification` (US-23.4). */
    @Suppress("TooGenericExceptionCaught")
    private fun uploadSlip() {
        val payment = mutableState.value.payment ?: return
        val upload = slip ?: return

        mutableState.update { it.copy(submitting = true, error = null) }
        val key = keys.next()

        viewModelScope.launch {
            try {
                apply(subscriptions.uploadSlip(payment.paymentId, upload, key))
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(submitting = false, error = ModeBErrors.messageFor(cause)) }
            }
        }
    }

    /**
     * Fetches the owner's LankaQR image, for the scan rail.
     *
     * Silent on failure: the step still carries the link, so the screen degrades to *"the QR could
     * not be loaded"* beside a payment that is perfectly valid — the passenger can still pay the
     * fleet another way, and a thrown error here would read as a failed payment.
     */
    @Suppress("TooGenericExceptionCaught")
    private fun loadQrImage() {
        val step = mutableState.value.step as? ModeBPaymentStep.ShowOwnerLankaQr ?: return
        viewModelScope.launch {
            mutableQrImage.value = try {
                subscriptions.ownerLankaQr(step.imageUrl)
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                null
            }
        }
    }

    /** Folds a server answer into the state. */
    private fun apply(payment: SubscriptionPayment) {
        mutableState.update { it.copy(submitting = false, payment = payment) }
    }

    private companion object {
        /** What the picker returns and what the slip is: a screenshot. */
        const val IMAGE_CONTENT_TYPE = "image/*"
    }
}
