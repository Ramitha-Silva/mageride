import Foundation
import MageRideShared

/// SCR-PI-025a's state.
///
/// ``fare`` and ``step`` are **derived, not stored**: a fare is the subscription's own
/// (`ModeBBilling.fareFor`) and a step is BR-23.10's answer for the chosen rail
/// (`ModeBPaymentRules.stepFor`). Copying either into a field would create a second value that can
/// disagree with the one it came from — which on a payment sheet means an amount or an account number
/// that is no longer the server's.
struct SubscriptionPayState {

    var subscription: Subscription?

    /// The chosen rail. Defaults to the wireframe's pre-selected LankaQR deep link.
    var method: SubscriptionPayMethod = SubscriptionRails.methods[0]

    /// The initiated payment, once **Confirm & pay** has been tapped. Its `payTo` is the only place
    /// the owner's collection details ever appear (AL-49).
    var payment: SubscriptionPayment?

    /// The attached screenshot's file name, for the row's caption. The bytes are held off the state —
    /// see ``SubscriptionPayModel``.
    var slipName: String?

    /// The owner's LankaQR image, for the scan rail. `nil` until it is fetched, and after a fetch
    /// that failed — the caption under the panel is what that produces.
    var ownerQr: Data?

    var isLoading = true
    var isSubmitting = false
    var errorKey: String?

    /// Whether the sheet can open a bank app for this rail on this handset (AL-15).
    ///
    /// Handed to `ModeBPaymentRules.stepFor` so that a handset with no bank app resolves the deep-link
    /// rail to the QR fallback rather than to a button that does nothing.
    var isBankAppAvailable = true

    /// What this subscriber owes. `nil` on a Free subscription, which has no payment UI at all.
    var fare: Money? { subscription.flatMap { ModeBBilling.shared.fareFor(subscription: $0) } }

    /// The amount actually being paid — the server's once there is a payment, the fare before.
    var amount: Money? { payment?.money ?? fare }

    /// What the sheet should do next. `nil` until the payment exists, because `payTo` does not.
    var step: ModeBPaymentStep? {
        payment.map {
            ModeBPaymentRules.shared.stepFor(method: method, payment: $0, bankAppAvailable: isBankAppAvailable)
        }
    }

    /// Whether **Confirm & pay** can fire.
    ///
    /// `online_transfer` is gated on the screenshot (US-23.4) — but only before initiation. After it,
    /// the passenger has the bank details this screen just gave them and the attach is the next step
    /// rather than a precondition.
    var canConfirm: Bool {
        guard subscription != nil, payment == nil, !isSubmitting else { return false }
        return !ModeBPaymentRules.shared.requiresSlip(method: method) || slipName != nil
    }

    /// The slip is still owed: the transfer was initiated and no screenshot has been accepted.
    var isAwaitingSlip: Bool {
        guard let payment, ModeBPaymentRules.shared.requiresSlip(method: method) else { return false }
        return payment.status == SubscriptionPaymentStatus.initiated
    }

    /// Nothing more to do here — the owner has it to verify, or it is already paid.
    var isSettled: Bool {
        guard let payment else { return false }
        return payment.status == SubscriptionPaymentStatus.paid
            || payment.status == SubscriptionPaymentStatus.pendingVerification
    }
}

/// SCR-PI-025a — paying one Mode B month, to the fleet owner.
///
/// **`payTo` is the OWNER's account and it only exists after the payment is initiated** (AL-49).
/// `POST /v1/mode-b/subscriptions/{id}/pay` is what mints it — a signed link to the owner's own
/// bank-app LankaQR image for the two QR rails, or bank/branch/account/holder for a transfer — and it
/// is served **only from a `verified` payout profile**, falling back to the last verified snapshot
/// rather than to an unverified edit. That is why this screen is two stages rather than one: the
/// chooser cannot show an account number it has not been given, and no client may invent one. A fleet
/// whose profile was never verified gets `409 payout-profile-not-verified`, which is the only honest
/// answer and has its own copy in ``ModeBErrors``.
///
/// **OnePay is gone** (AL-59). The wireframe still draws it at +5 %, which would have routed a
/// passenger's payment into MageRide's merchant account instead of the fleet's — see
/// ``SubscriptionRails``. Nothing on this screen carries a surcharge.
///
/// **Two of the four rails settle on a human, not a webhook.** An `online_transfer` sits at
/// `pending_verification` until the owner confirms the slip (US-23.4), and `cash` is handed to a
/// collector and marked received by the owner in the portal (US-23.6) — `POST …/mark-cash` is theirs
/// and answers `403 not-owner` here. Neither can be completed from this handset, and the screen says
/// so rather than pretending to wait.
@MainActor
final class SubscriptionPayModel: ObservableObject {

    @Published private(set) var state = SubscriptionPayState()

    private let subscriptionId: String
    private let subscriptions: SubscriptionRepository
    private let sessions: PassengerSessions
    private let bank: BankAppHandoff
    private let keys: IdempotencyKeys

    /// The attached screenshot's bytes.
    ///
    /// Held off ``state`` because `Data` of a couple of megabytes on an `@Published` value is a
    /// couple of megabytes SwiftUI compares on every unrelated update. The file *name* is on the
    /// state, because that is what the row draws.
    private var slip: Data?

    init(
        subscriptionId: String,
        subscriptions: SubscriptionRepository,
        sessions: PassengerSessions,
        bank: BankAppHandoff,
        keys: IdempotencyKeys
    ) {
        self.subscriptionId = subscriptionId
        self.subscriptions = subscriptions
        self.sessions = sessions
        self.bank = bank
        self.keys = keys
    }

    /// Finds the subscription being paid.
    ///
    /// `GET /v1/mode-b/subscriptions/{passengerId}` is the only read that carries one, and it is a
    /// list — **there is no `GET …/subscriptions/{subscriptionId}`** on this contract. So the screen
    /// reads the passenger's own subscriptions and picks. C082 recorded the gap; the C100 handoff
    /// restates it.
    func load() async {
        guard let passengerId = sessions.userId else {
            state.isLoading = false
            return
        }

        do {
            let held = try await subscriptions.subscriptions(passengerId: passengerId)
            state.isLoading = false
            state.subscription = held.first { $0.subscriptionId == subscriptionId }
        } catch is CancellationError {
            return
        } catch {
            state.isLoading = false
            state.errorKey = ModeBErrors.messageKey(for: error)
        }
    }

    func choose(_ method: SubscriptionPayMethod) {
        // A rail change before initiation only; afterwards the payment row is already typed with the
        // method the server accepted, and switching would silently orphan it.
        guard state.payment == nil else { return }
        state.method = method
        state.errorKey = nil
    }

    /// The picker came back with a screenshot of the transfer slip (US-23.4).
    ///
    /// Attached **after** initiation, the upload follows immediately: the passenger transferred the
    /// money using the details this screen showed them, and the upload is the only thing still owed.
    func attachSlip(fileName: String, data: Data) async {
        slip = data
        state.slipName = fileName
        state.errorKey = nil
        if state.isAwaitingSlip { await uploadSlip() }
    }

    func clearError() {
        state.errorKey = nil
    }

    /// **Confirm & pay** — initiates the month and learns where the money goes.
    ///
    /// The idempotency key is minted once here, so a double tap is one payment row rather than two
    /// months of debt (R-14). A slip attached before the tap is uploaded straight after, which is the
    /// order a passenger who has already transferred the money will be in.
    func confirm() async {
        guard state.canConfirm else { return }
        let method = state.method
        state.isSubmitting = true
        state.errorKey = nil
        let key = keys.next()

        do {
            let payment = try await subscriptions.pay(
                subscriptionId: subscriptionId,
                method: method,
                idempotencyKey: key
            )
            state.isSubmitting = false
            state.payment = payment
        } catch is CancellationError {
            state.isSubmitting = false
            return
        } catch {
            state.isSubmitting = false
            state.errorKey = ModeBErrors.messageKey(for: error)
            return
        }

        if state.isAwaitingSlip, slip != nil {
            await uploadSlip()
        } else {
            await loadOwnerQr()
        }
    }

    /// AL-15's hand-off, on the deep-link rail.
    ///
    /// A refusal is not an error state and does not raise one: `isBankAppAvailable` going `false`
    /// re-resolves the step to `ShowLankaQrFallback`, which is the payload the passenger scans with
    /// their own bank app instead. The ordering is AL-15's — the deep link is the primary path and the
    /// code is the fallback, never the other way round.
    func openBankApp(url: String) async {
        state.isBankAppAvailable = await bank.openBankApp(url: url)
    }

    // MARK: -

    /// Uploads the slip and moves the payment to `pending_verification` (US-23.4).
    private func uploadSlip() async {
        guard let payment = state.payment, let data = slip else { return }
        let fileName = state.slipName ?? SubscriptionPayModel.slipFileName

        state.isSubmitting = true
        state.errorKey = nil
        let key = keys.next()

        do {
            let updated = try await subscriptions.uploadSlip(
                paymentId: payment.paymentId,
                fileName: fileName,
                data: data,
                idempotencyKey: key
            )
            state.isSubmitting = false
            state.payment = updated
        } catch is CancellationError {
            state.isSubmitting = false
        } catch {
            state.isSubmitting = false
            state.errorKey = ModeBErrors.messageKey(for: error)
        }
    }

    /// Fetches the owner's LankaQR image, for the scan rail.
    ///
    /// Silent on failure: the step still carries the link, so the screen degrades to *"the fleet's
    /// code could not be loaded"* beside a payment that is perfectly valid — the passenger can still
    /// pay the fleet another way, and a thrown error here would read as a failed payment.
    private func loadOwnerQr() async {
        // Unwrapped before the cast: an `as?` applied to an `Optional` is a conditional cast Swift
        // accepts and reasons about differently between releases, and this one decides whether a
        // passenger is shown a QR at all.
        guard
            let current = state.step,
            let step = current as? ModeBPaymentStepShowOwnerLankaQr
        else {
            return
        }
        state.ownerQr = try? await subscriptions.ownerLankaQr(link: step.imageUrl)
    }

    /// The name a slip is sent under when the picker exposed none. Not user-facing, so it is a Swift
    /// constant rather than three identical `.strings` values.
    private static let slipFileName = "transfer-slip.jpg"
}
