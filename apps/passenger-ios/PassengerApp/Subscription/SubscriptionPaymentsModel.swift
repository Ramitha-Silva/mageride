import Foundation
import MageRideShared

/// SCR-PI-025b's state.
struct SubscriptionPaymentsState {

    var subscription: Subscription?

    /// Newest month first. Ordered here rather than trusted from the wire — the contract fixes no
    /// order on `GET …/payments`, and a statement that listed April above June would read as a missing
    /// payment.
    var payments: [SubscriptionPayment] = []

    var isLoading = true
    var errorKey: String?

    /// The vehicle's monthly fare, for the header. `nil` on a Free subscription.
    var fare: Money? { subscription.flatMap { ModeBBilling_.shared.fareFor(subscription: $0) } }

    /// Genuinely empty rather than still loading.
    var isEmpty: Bool { !isLoading && payments.isEmpty }
}

/// SCR-PI-025b — one subscription's statement (US-23.9).
///
/// **It is the passenger's half of a ledger the fleet owner also reads.**
/// `GET /v1/mode-b/subscriptions/{id}/payments` and the owner's
/// `GET /v1/mode-b/{vehicleId}/subscribers/{subId}/payments` answer the same rows from the same table
/// — which is why a month the owner has not yet confirmed shows *Pending verification* here rather
/// than nothing at all. A passenger who transferred the money on the 6th and is told they have not
/// paid is the failure this status exists to prevent (US-23.4).
///
/// **Cash is a row like any other, once it exists.** It appears only after the owner marks it received
/// in the portal (US-23.6) — there is no passenger-side write for it — so an absent cash month means
/// the owner has not recorded it, not that the money was never handed over.
@MainActor
final class SubscriptionPaymentsModel: ObservableObject {

    @Published private(set) var state = SubscriptionPaymentsState()

    private let subscriptionId: String
    private let subscriptions: SubscriptionRepository
    private let sessions: PassengerSessions

    init(subscriptionId: String, subscriptions: SubscriptionRepository, sessions: PassengerSessions) {
        self.subscriptionId = subscriptionId
        self.subscriptions = subscriptions
        self.sessions = sessions
    }

    /// Reads the statement, then fills in the header. Also what `.refreshable` calls.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil

        do {
            let rows = try await subscriptions.payments(subscriptionId: subscriptionId)
            state.isLoading = false
            state.payments = rows.sorted { SubscriptionPeriod.isBefore($1.periodMonth, $0.periodMonth) }
        } catch is CancellationError {
            return
        } catch {
            state.isLoading = false
            state.errorKey = ModeBErrors.messageKey(for: error)
            return
        }

        await loadSubscription()
    }

    func clearError() {
        state.errorKey = nil
    }

    // MARK: -

    /// Fills in the header's vehicle and fare.
    ///
    /// A second read, because the statement carries neither: `SubscriptionPayment` has a
    /// `subscriptionId` and an amount per row, and the wireframe's header wants the vehicle and the
    /// standing monthly fare. Silent on failure — the statement is what the passenger opened, and
    /// losing a header is not worth losing it over.
    private func loadSubscription() async {
        guard let passengerId = sessions.userId else { return }
        guard let held = try? await subscriptions.subscriptions(passengerId: passengerId) else { return }
        state.subscription = held.first { $0.subscriptionId == subscriptionId }
    }
}
