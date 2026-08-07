import Foundation
import MageRideShared

/// One card on SCR-PI-025.
struct SubscriptionCard: Identifiable, Equatable {

    /// The grant itself.
    let subscription: Subscription

    /// What this subscriber owes each month — `nil` on a **Free** vehicle, which is
    /// `ModeBBilling.fareFor`'s answer and not an interpretation of a missing field.
    var fare: Money?

    /// Where the current month stands, from the latest payment row. `nil` while the statement is
    /// still loading, or when it could not be read — the pill says *Checking…* rather than guessing,
    /// because "Paid" shown wrongly is the one error a passenger acts on.
    var monthStatus: SubscriberMonthStatus?

    var id: String { subscription.subscriptionId }

    /// The wireframe's 💳 Pay and 🧾 rows are **Paid only** — a Free vehicle has no payment UI.
    var isPaid: Bool { subscription.billing == SubscriptionBilling.paid }

    static func == (lhs: SubscriptionCard, rhs: SubscriptionCard) -> Bool {
        // On the subscription id and the two derived fields, not on `Subscription` itself: an
        // exported Kotlin data class compares by `isEqual:`, which is identity for a type
        // Kotlin/Native did not synthesise `equals` onto the Objective-C side of.
        lhs.id == rhs.id
            && lhs.fare?.amountMinor == rhs.fare?.amountMinor
            && lhs.monthStatus == rhs.monthStatus
    }
}

/// SCR-PI-025's state.
struct SubscriptionsState {

    var cards: [SubscriptionCard] = []
    var isLoading = true

    /// The card whose ✕ (or swipe) was answered, awaiting the confirm. Unsubscribing is not undoable
    /// in place — see ``SubscriptionsModel/unsubscribe(_:)``.
    var confirming: SubscriptionCard?

    /// The subscription whose unsubscribe is in flight.
    var leaving: String?

    var errorKey: String?

    /// Genuinely empty rather than still loading — the wireframe's *"no private vehicles yet"* body.
    var isEmpty: Bool { !isLoading && cards.isEmpty }
}

/// SCR-PI-025 — the vehicles this passenger can see, and what they owe for them.
///
/// **Unsubscribing is one tap and it is not reversible in place** (AL-25, US-23.11). The grant goes,
/// `share.revoked` reaches fanout-svc, the vehicle leaves the map, and coming back means asking the
/// owner again through SCR-PI-024 and waiting for an accept. That is why the ✕ raises a confirm first
/// — the same argument C098 made for the Rs 50 cancellation fee: the moment before the tap is the only
/// moment the passenger can be told what it costs.
///
/// **The map is cleared here, not by the socket.** ``PassengerLiveMap/dropVehicle(_:)`` erases the
/// marker on the response rather than waiting for the directed `ShareRevoked` to come back; D-22
/// promises that push in under 200 ms, and this makes the screen honest even when it does not arrive.
/// Nothing is sent to the hub — `signalr-hub.md` §2 has four client → server methods and none of them
/// leaves a `vehicle:{vehicleId}` group, because membership is the server's (D-23).
///
/// **The card's Paid/Payment-due pill costs one read per Paid subscription, and there is no cheaper
/// honest answer.** `Subscription` carries `nextDue` and `status` but nothing about the current month:
/// `SubscriberMonthStatus` lives on `SubscriberRow`, which is the **owner's** roster and answers
/// `403 not-owner` here. So the pill comes from the subscription's own statement — `GET …/payments`,
/// latest period — through `ModeBPaymentRules.monthStatus`, which is also what makes *"an
/// online-transfer payment shows Pending verification"* true on this screen and not only on
/// SCR-PI-025b. Deriving it from `nextDue` instead would call an unpaid first month "Paid", because
/// the server advances that date on payment and not on join.
@MainActor
final class SubscriptionsModel: ObservableObject {

    @Published private(set) var state = SubscriptionsState()

    private let subscriptions: SubscriptionRepository
    private let sessions: PassengerSessions
    private let live: PassengerLiveMap
    private let keys: IdempotencyKeys

    init(
        subscriptions: SubscriptionRepository,
        sessions: PassengerSessions,
        live: PassengerLiveMap,
        keys: IdempotencyKeys
    ) {
        self.subscriptions = subscriptions
        self.sessions = sessions
        self.live = live
        self.keys = keys
    }

    /// Reads the cards, then fills in their pills. Also what `.refreshable` calls.
    func refresh() async {
        guard let passengerId = sessions.userId else {
            state.isLoading = false
            return
        }

        state.isLoading = true
        state.errorKey = nil

        let held: [Subscription]
        do {
            held = try await subscriptions.subscriptions(passengerId: passengerId)
        } catch is CancellationError {
            return
        } catch {
            state.isLoading = false
            state.errorKey = ModeBErrors.messageKey(for: error)
            return
        }

        // Drawn without the pills first: the list is what the passenger came for, and holding it back
        // for N statements would leave the screen empty while they load.
        state.cards = held.map {
            SubscriptionCard(subscription: $0, fare: ModeBBilling.shared.fareFor(subscription: $0), monthStatus: nil)
        }
        state.isLoading = false

        await loadMonthStatuses()
    }

    func clearError() {
        state.errorKey = nil
    }

    /// The ✕ or the swipe was answered. Nothing has happened yet — ``unsubscribe(_:)`` is what acts.
    func confirmUnsubscribe(_ card: SubscriptionCard) {
        state.confirming = card
    }

    func dismissConfirm() {
        state.confirming = nil
    }

    /// Leaves the vehicle (US-23.11).
    ///
    /// The row is dropped from the list on success rather than re-read: `GET …/subscriptions` answers
    /// a passenger's own grants and a cancelled one is gone from it, so a refetch would cost a round
    /// trip to learn what the response already said. A **failure leaves the card where it was** — a
    /// passenger whose unsubscribe did not land still has the subscription, and hiding it would mean
    /// they could not try again.
    ///
    /// The roster row on the **owner's** side survives, muted, until they delete it (US-23.12) —
    /// `DELETE /v1/mode-b/{vehicleId}/subscribers/{id}` is theirs, and nothing on this surface can or
    /// should reach it.
    func unsubscribe(_ card: SubscriptionCard) async {
        let subscriptionId = card.subscription.subscriptionId
        state.confirming = nil
        state.leaving = subscriptionId
        state.errorKey = nil
        let key = keys.next()

        do {
            _ = try await subscriptions.unsubscribe(subscriptionId: subscriptionId, idempotencyKey: key)
        } catch is CancellationError {
            state.leaving = nil
            return
        } catch {
            state.leaving = nil
            state.errorKey = ModeBErrors.messageKey(for: error)
            return
        }

        // D-22's half the client owns. See the type's note.
        live.dropVehicle(card.subscription.vehicleId)
        state.cards.removeAll { $0.id == subscriptionId }
        state.leaving = nil
    }

    // MARK: -

    /// Fills in every Paid card's pill from its own statement.
    ///
    /// One task per card, all in flight at once: five subscriptions are five statements and running
    /// them in sequence would make the last pill land five round trips after the first. The results
    /// are folded back on the main actor by id rather than by position, because a card can have been
    /// unsubscribed while its statement was in the air.
    ///
    /// A failure is swallowed — the card keeps its `nil` pill, which is what it was already showing,
    /// and one unreadable statement must not blank a list of five subscriptions.
    private func loadMonthStatuses() async {
        let paid = state.cards.filter(\.isPaid).map(\.id)
        guard !paid.isEmpty else { return }

        let statuses = await withTaskGroup(of: (String, SubscriberMonthStatus?).self) { group in
            for subscriptionId in paid {
                group.addTask { [subscriptions] in
                    // The latest row is taken by `periodMonth` rather than by list position: the
                    // contract fixes no ordering on `GET …/payments`, and "the first item" would be a
                    // guess that happens to be right against a server that sorts newest-first.
                    guard let rows = try? await subscriptions.payments(subscriptionId: subscriptionId) else {
                        return (subscriptionId, nil)
                    }
                    let latest = rows.max { SubscriptionPeriod.isBefore($0.periodMonth, $1.periodMonth) }
                    return (subscriptionId, ModeBPaymentRules.shared.monthStatus(payment: latest))
                }
            }

            var resolved: [String: SubscriberMonthStatus] = [:]
            for await (subscriptionId, status) in group {
                if let status { resolved[subscriptionId] = status }
            }
            return resolved
        }

        for index in state.cards.indices {
            if let status = statuses[state.cards[index].id] {
                state.cards[index].monthStatus = status
            }
        }
    }
}

/// Ordering two `period_month` values without asking Kotlin to compare them.
///
/// `BusinessDate` is a `kotlinx.datetime.LocalDate`; it is `Comparable` in Kotlin, but `compareTo`
/// reaches Objective-C as a method on a type whose comparison semantics the bridge does not carry into
/// Swift's `Comparable`. What it *does* carry is `description`, which is ISO-8601 — and `YYYY-MM-DD`
/// sorts lexicographically in exactly calendar order, zero-padded on both fields. So the comparison is
/// a string comparison, and it is written once here because both SCR-PI-025 and SCR-PI-025b make it.
enum SubscriptionPeriod {

    /// Whether `lhs` is an earlier month than `rhs`.
    static func isBefore(_ lhs: BusinessDate, _ rhs: BusinessDate) -> Bool {
        String(describing: lhs) < String(describing: rhs)
    }
}
