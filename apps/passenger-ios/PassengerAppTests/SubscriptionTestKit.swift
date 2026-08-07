import Foundation
import MageRideShared

@testable import PassengerApp

/// Cluster 6's seam, faked, plus the fixtures its suites are written against.
///
/// Same rule as ``OnboardingTestKit``, ``HomeTestKit``, ``BookingTestKit``, ``RideTestKit`` and
/// ``HistoryTestKit``: **this stands in for a Swift protocol, never for a Kotlin class.**
/// ``SubscriptionRepository`` exists precisely because `SubscriptionApi` is a Kotlin interface with
/// `suspend` methods that Swift cannot implement.
///
/// **A hand-written fake rather than a fake backend**, the call `apps/passenger-android` made for the
/// same cluster: what these models decide is *which* calls they make and in what order — the
/// unsubscribe that must reach the live map, the pay that must precede a slip upload, the statement
/// read that fills a pill — and recording the calls is the assertion. The HTTP layer between them is
/// C013's and C019 already tests it.
///
/// **The one boxed Kotlin primitive in this cluster is `KotlinLong`**, for `Subscription`'s optional
/// `monthlyFareMinor`, and the spelling is ``HistoryFixtures``' — `initWithLongLong:` →
/// `init(longLong:)`, the name the Objective-C importer derives from the generated initialiser. If
/// the existing `KotlinInt(int:)` in this target is right then so is this, and if it is wrong both
/// are one edit away (the C096 finding, which records that `apps/driver-ios` spells the same
/// constructors `init(value:)`).

// MARK: - The repository

final class FakeSubscriptionRepository: SubscriptionRepository, @unchecked Sendable {

    /// Programmable answers.
    ///
    /// Named `held` rather than `subscriptions` because ``subscriptions(passengerId:)`` is a method on
    /// this type — the trap ``ApiHistoryRepository`` records about its own `rideApi`, and not one to
    /// leave to a compiler nobody here can run.
    var held: [Subscription] = []
    var payments: [SubscriptionPayment] = []
    var accessRequestStatus: AccessRequestStatus = AccessRequestStatus.pending
    var payAnswer: SubscriptionPayment?
    var slipAnswer: SubscriptionPayment?
    var qrBytes: Data?

    /// Thrown by the **next** call to any operation, then cleared. One-shot, exactly as the Android
    /// fake's `failWith` is, so a suite can arm a failure after a successful load.
    var failWith: Error?

    /// Every call, in order — several rules here are about *what was asked* rather than about state.
    private(set) var accessRequested: [String] = []
    private(set) var subscriptionReads: [String] = []
    private(set) var unsubscribed: [String] = []

    /// A named type rather than a tuple, because **Swift has no key paths into tuples** — the C087
    /// finding `apps/driver-ios` records, and `paid.map(\.method)` is exactly the expression it bites.
    private(set) var paid: [PaidCall] = []
    private(set) var slipsUploaded: [String] = []
    private(set) var paymentReads: [String] = []
    private(set) var qrLinksFetched: [String] = []
    private(set) var idempotencyKeys: [String?] = []

    func requestAccess(vehicleId: String, note: String?, idempotencyKey: String?) async throws -> AccessRequest {
        try raise()
        accessRequested.append(vehicleId)
        idempotencyKeys.append(idempotencyKey)
        return AccessRequest(
            requestId: SubscriptionFixtures.requestId,
            vehicleId: vehicleId,
            passengerId: SubscriptionFixtures.passengerId,
            passengerName: nil,
            passengerMobileMasked: nil,
            status: accessRequestStatus,
            createdAt: SubscriptionFixtures.timestamp()
        )
    }

    func subscriptions(passengerId: String) async throws -> [Subscription] {
        try raise()
        subscriptionReads.append(passengerId)
        return held
    }

    func unsubscribe(subscriptionId: String, idempotencyKey: String?) async throws -> Subscription {
        try raise()
        unsubscribed.append(subscriptionId)
        idempotencyKeys.append(idempotencyKey)
        guard let current = held.first(where: { $0.subscriptionId == subscriptionId }) else {
            throw SubscriptionFakeError.unreachable
        }
        return current
    }

    func pay(
        subscriptionId: String,
        method: SubscriptionPayMethod,
        idempotencyKey: String?
    ) async throws -> SubscriptionPayment {
        try raise()
        paid.append(PaidCall(subscriptionId: subscriptionId, method: method))
        idempotencyKeys.append(idempotencyKey)
        return payAnswer ?? SubscriptionFixtures.payment(
            method: method,
            status: SubscriptionPaymentStatus.initiated
        )
    }

    func uploadSlip(
        paymentId: String,
        fileName: String,
        data: Data,
        idempotencyKey: String?
    ) async throws -> SubscriptionPayment {
        try raise()
        slipsUploaded.append(paymentId)
        idempotencyKeys.append(idempotencyKey)
        return slipAnswer ?? SubscriptionFixtures.payment(
            method: SubscriptionPayMethod.onlineTransfer,
            status: SubscriptionPaymentStatus.pendingVerification
        )
    }

    func payments(subscriptionId: String) async throws -> [SubscriptionPayment] {
        try raise()
        paymentReads.append(subscriptionId)
        return payments
    }

    /// Deliberately outside ``raise()``, exactly as the Android fake is: the QR read is best-effort
    /// and the models swallow its failure, so a suite arming `failWith` for a *pay* must not have it
    /// consumed by the fetch that follows.
    func ownerLankaQr(link: String) async throws -> Data? {
        qrLinksFetched.append(link)
        return qrBytes
    }

    private func raise() throws {
        guard let failure = failWith else { return }
        failWith = nil
        throw failure
    }
}

/// One `POST /v1/mode-b/subscriptions/{id}/pay`, recorded.
struct PaidCall: Equatable {
    let subscriptionId: String
    let method: SubscriptionPayMethod
}

/// Idempotency keys a test can pin. R-14's rule is *"the same key across a retry"*, and asserting it
/// needs a generator whose answers are known.
final class FakeIdempotencyKeys: IdempotencyKeys {

    private(set) var issued: [String] = []

    func next() -> String {
        let key = "01JIDEMPOTENCY00000000\(String(format: "%04d", issued.count))"
        issued.append(key)
        return key
    }
}

// MARK: -

enum SubscriptionFixtures {

    static let passengerId = "01JPAX00000000000000000001"
    static let vehicleId = "01JVEH00000000000000000001"
    static let freeVehicleId = "01JVEH00000000000000000002"
    static let subscriptionId = "01JSUB00000000000000000001"
    static let freeSubscriptionId = "01JSUB00000000000000000002"
    static let requestId = "01JREQ00000000000000000001"
    static let paymentId = "01JPAY00000000000000000001"
    static let payoutProfileId = "01JPRF00000000000000000001"

    /// Rs 6,000 a month — the wireframe's Office Van.
    static let monthlyFareMinor: Int64 = 600_000

    /// 2026-06-01 12:00 in Colombo, so the business date is the **first of the month** — the shape
    /// `ck_payments_period_month_first` CHECKs a `period_month` into.
    static let juneMillis: Int64 = 1_780_295_400_000
    static let mayMillis: Int64 = 1_777_617_000_000
    static let aprilMillis: Int64 = 1_775_025_000_000

    /// 2026-06-06 10:15 in Colombo — a `paidAt`, which the statement row prints as `6 Jun`.
    static let paidAtMillis: Int64 = 1_780_721_100_000

    /// 2026-07-06 in Colombo — the wireframe's *"next due 6 Jul"*. It is a JOIN_ANNIVERSARY due date:
    /// joined 5 June, one calendar month, plus a day (BR-23.9's worked example, which C016 followed).
    static let nextDueMillis: Int64 = 1_783_319_400_000

    static func timestamp(_ millis: Int64 = paidAtMillis) -> Timestamp {
        IosInstantKt.timestampFromEpochMillis(millis: millis)
    }

    /// A Colombo business date from an instant.
    ///
    /// Through `:shared` rather than a `DateFormatter` here, for the reason `IosBusinessDate.kt` gives:
    /// `BusinessCalendar.businessDate` takes its zone as a defaulted parameter and a default argument
    /// does not survive the export, so the alternative is naming `"Asia/Colombo"` a second time.
    static func businessDate(_ millis: Int64) -> BusinessDate {
        IosBusinessDateKt.colomboBusinessDateOf(at: timestamp(millis))
    }

    /// AL-49's verified block, as `POST …/pay` returns it.
    static let payTo = PayTo(
        lankaqrImageUrl: "https://api.mageride.lk/v1/mode-b/files/lankaqr/"
            + payoutProfileId + "?expires=1780000000&signature=abc123",
        bank: "Commercial Bank",
        branch: "Nugegoda",
        accountNo: "8001234567",
        accountHolderName: "ABC Fleet (Pvt) Ltd"
    )

    static func paidSubscription(
        subscriptionId: String = subscriptionId,
        vehicleId: String = vehicleId,
        nextDue: BusinessDate? = businessDate(nextDueMillis)
    ) -> Subscription {
        Subscription(
            subscriptionId: subscriptionId,
            vehicleId: vehicleId,
            passengerId: passengerId,
            billing: SubscriptionBilling.paid,
            monthlyFareMinor: KotlinLong(longLong: monthlyFareMinor),
            currency: Currency.lkr,
            cycle: SubscriptionCycle.monthFirst,
            joinDay: nil,
            nextDue: nextDue,
            nextDueTzAt: nil,
            status: SubscriptionStatus.active
        )
    }

    /// Office transport: no fare at all (`ck_subscriptions_fare`).
    static func freeSubscription() -> Subscription {
        Subscription(
            subscriptionId: freeSubscriptionId,
            vehicleId: freeVehicleId,
            passengerId: passengerId,
            billing: SubscriptionBilling.free,
            monthlyFareMinor: nil,
            currency: nil,
            cycle: SubscriptionCycle.monthFirst,
            joinDay: nil,
            nextDue: nil,
            nextDueTzAt: nil,
            status: SubscriptionStatus.active
        )
    }

    static func payment(
        method: SubscriptionPayMethod,
        status: SubscriptionPaymentStatus,
        paymentId: String = paymentId,
        monthMillis: Int64 = juneMillis,
        redirectUrl: String? = "lankaqr://pay?ref=abc",
        qrPayload: String? = "00020101021230…"
    ) -> SubscriptionPayment {
        SubscriptionPayment(
            paymentId: paymentId,
            subscriptionId: subscriptionId,
            method: method,
            amountMinor: monthlyFareMinor,
            currency: Currency.lkr,
            status: status,
            periodMonth: businessDate(monthMillis),
            periodMonthTzAt: nil,
            // AL-49: the owner's collection block exists on an **initiated** payment and nowhere else.
            payTo: status == SubscriptionPaymentStatus.initiated ? payTo : nil,
            redirectUrl: redirectUrl,
            qrPayload: qrPayload,
            slipUrl: nil,
            paidAt: status == SubscriptionPaymentStatus.paid ? timestamp() : nil
        )
    }
}

/// A failure with nothing behind it — what an unreachable service looks like to a model.
enum SubscriptionFakeError: Error {
    case unreachable
}
