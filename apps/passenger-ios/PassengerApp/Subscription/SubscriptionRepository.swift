import Foundation
import MageRideShared

/// Cluster 6's seam — Mode B as a **passenger** touches it.
///
/// `subscription.yaml` is one contract carrying two unrelated money flows: the driver's daily platform
/// fee and reseller credit, and this — a passenger subscribing to a private vehicle and paying its
/// owner monthly. Only the second half is behind this protocol, and the omissions are the point:
///
/// - **The roster operations are the OWNER's.** `listModeBSubscribers`, `setSubscriberFare`,
///   `markSubscriberCashPaid`, `deleteModeBSubscriber` and `confirmTransferSlip` all answer
///   `403 not-owner` to a passenger bearer, and the Fleet Portal is where they are called. A passenger
///   app that carried them would be a screen away from asking.
/// - **Accept and reject are the driver's/owner's** (SCR-DI-028, SCR-FP-011). This side only raises
///   the request; AL-25's *"re-joining requires request → accept"* is a rule about two surfaces, and
///   this is the one that asks.
///
/// **Money here never touches the platform ledger** (AL-24, §18b). A subscription payment goes to the
/// fleet owner's own account — `payTo`, served only from a `verified` payout profile (AL-49) — and
/// MageRide holds none of it. That is why nothing in this cluster reaches wallet-svc.
///
/// A Swift protocol rather than `SubscriptionApi` itself, for the rule `apps/passenger-ios/CLAUDE.md`
/// states: **this app implements two Kotlin interfaces and no more.** `SubscriptionApi` is a Kotlin
/// interface with `suspend` methods and Swift can stand in for none of them. **No caching and no
/// state** — the screens' state is theirs. `apps/passenger-android`'s `SubscriptionRepository` is the
/// same six operations plus the same signed-link read.
protocol SubscriptionRepository: AnyObject {

    /// `POST /v1/mode-b/{vehicleId}/access-requests` — ask this vehicle's owner for access.
    func requestAccess(vehicleId: String, note: String?, idempotencyKey: String?) async throws -> AccessRequest

    /// `GET /v1/mode-b/subscriptions/{passengerId}` — SCR-PI-025's cards.
    ///
    /// The page is unwrapped here rather than at a screen, for ``HistoryRepository/rides()``'s reason:
    /// neither SCR-PI-025 nor SCR-PI-025a draws a *"load more"* control, so a cursor nothing reads
    /// would be a field three models had to carry.
    func subscriptions(passengerId: String) async throws -> [Subscription]

    /// `POST /v1/mode-b/subscriptions/{id}/unsubscribe` — leave, and lose sight of the vehicle.
    func unsubscribe(subscriptionId: String, idempotencyKey: String?) async throws -> Subscription

    /// `POST /v1/mode-b/subscriptions/{id}/pay` — initiate a month and learn where to send it.
    func pay(
        subscriptionId: String,
        method: SubscriptionPayMethod,
        idempotencyKey: String?
    ) async throws -> SubscriptionPayment

    /// `POST /v1/mode-b/payments/{paymentId}/transfer-slip` — the screenshot US-23.4 requires.
    ///
    /// Takes the bytes rather than a `FileUpload`, which is where this protocol departs from the
    /// Android twin's shape: `FileUpload` holds a Kotlin `ByteArray`, so building one is a bridge
    /// crossing (see ``ApiSubscriptionRepository/uploadSlip(paymentId:fileName:data:idempotencyKey:)``)
    /// and a model that assembled one would be a model a test cannot construct an argument for.
    func uploadSlip(
        paymentId: String,
        fileName: String,
        data: Data,
        idempotencyKey: String?
    ) async throws -> SubscriptionPayment

    /// `GET /v1/mode-b/subscriptions/{id}/payments` — SCR-PI-025b's statement.
    func payments(subscriptionId: String) async throws -> [SubscriptionPayment]

    /// The owner's bank-app LankaQR image, as bytes (AL-49).
    ///
    /// Takes the signed link `payTo.lankaqrImageUrl` carries and answers the image behind it, or
    /// `nil` when the link is not one this build understands. See ``SignedFileLink`` for why the app
    /// re-derives the call rather than handing the URL to an image loader.
    func ownerLankaQr(link: String) async throws -> Data?
}

/// ``SubscriptionRepository`` over C013's generated `subscription-svc` client.
///
/// The whole of what it adds is what a Swift call site cannot leave out: **every parameter is
/// passed**, because a Kotlin default argument does not survive the Objective-C export — which is why
/// `page:` is spelled `PageRequest.companion.FIRST` at each read rather than omitted.
final class ApiSubscriptionRepository: SubscriptionRepository {

    private let subscriptionApi: SubscriptionApi

    init(subscriptions: SubscriptionApi) {
        self.subscriptionApi = subscriptions
    }

    func requestAccess(vehicleId: String, note: String?, idempotencyKey: String?) async throws -> AccessRequest {
        try await subscriptionApi.requestModeBAccess(
            vehicleId: vehicleId,
            request: RequestModeBAccessRequest(
                note: note.flatMap { $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : $0 }
            ),
            idempotencyKey: idempotencyKey
        )
    }

    /// **`Page<T>.items` arrives as `[Any]`**, here and on ``payments(subscriptionId:)``. The export
    /// emits the property as `NSArray<id>` — `Page<T>` keeps its type parameter across the bridge and
    /// its rows do not — so the concrete type is put back at each of the two paged reads.
    /// `compactMap` rather than `as!` for this target's own reason: a failed force cast raises an
    /// exception Swift cannot catch and the process terminates. Every row is a `Subscription` (and
    /// below, a `SubscriptionPayment`) by contract, so nothing is dropped.
    func subscriptions(passengerId: String) async throws -> [Subscription] {
        try await subscriptionApi.listPassengerSubscriptions(
            passengerId: passengerId,
            page: PageRequest.companion.FIRST
        ).items.compactMap { $0 as? Subscription }
    }

    func unsubscribe(subscriptionId: String, idempotencyKey: String?) async throws -> Subscription {
        try await subscriptionApi.unsubscribeModeB(subscriptionId: subscriptionId, idempotencyKey: idempotencyKey)
    }

    func pay(
        subscriptionId: String,
        method: SubscriptionPayMethod,
        idempotencyKey: String?
    ) async throws -> SubscriptionPayment {
        try await subscriptionApi.payModeBSubscription(
            subscriptionId: subscriptionId,
            // `periodMonth` is deliberately left absent: the contract defaults it to "the next due
            // period", which is server-side Asia/Colombo arithmetic (D-38). A client that computed
            // the month from the handset would pay the wrong one for five and a half hours a day.
            request: PayModeBSubscriptionRequest(method: method, periodMonth: nil),
            idempotencyKey: idempotencyKey
        )
    }

    /// Uploads the screenshot, converting the bytes **in Kotlin**.
    ///
    /// `IosCapturedDocumentKt.fileUploadOf`, not a `FileUpload` built here: that type takes a
    /// `ByteArray`, whose only Swift-facing mutator is one Objective-C message per byte, and a
    /// screenshot off a modern handset is a couple of megabytes of them. Kotlin does it with one
    /// `memcpy`. The same helper `apps/driver-ios`'s support screenshot goes through, and it is the
    /// **provenance-free** form on purpose: a transfer slip is not a `docs.uploads` document and the
    /// contract declares no `capturedVia` part beside it (AL-43).
    ///
    /// `data:` takes the `Data` as it is: the helper's Kotlin parameter is an `NSData`, and the
    /// importer bridges an Objective-C `NSData *` parameter to `Data` on the Swift side — so an
    /// `as NSData` here is a cast *away* from what the call wants, not towards it.
    func uploadSlip(
        paymentId: String,
        fileName: String,
        data: Data,
        idempotencyKey: String?
    ) async throws -> SubscriptionPayment {
        try await subscriptionApi.uploadTransferSlip(
            paymentId: paymentId,
            file: IosCapturedDocumentKt.fileUploadOf(
                fileName: fileName,
                data: data,
                contentType: ApiSubscriptionRepository.slipContentType
            ),
            idempotencyKey: idempotencyKey
        )
    }

    func payments(subscriptionId: String) async throws -> [SubscriptionPayment] {
        try await subscriptionApi.listSubscriptionPayments(
            subscriptionId: subscriptionId,
            page: PageRequest.companion.FIRST
        ).items.compactMap { $0 as? SubscriptionPayment }
    }

    /// Fetches the owner's QR through the typed client rather than through an image loader.
    ///
    /// The bytes come back as a `KotlinByteArray` and are copied with `IosBytesKt.nsDataOf` — one
    /// `memcpy` rather than one Objective-C message per byte, which is the trade `IosBytes.kt` was
    /// added for on the driver side.
    func ownerLankaQr(link: String) async throws -> Data? {
        guard let parsed = SignedFileLink.parse(link), parsed.kind == ModeBFileKind.lankaqr else { return nil }
        let bytes = try await subscriptionApi.getModeBFile(
            kind: parsed.kind,
            id: parsed.id,
            expires: parsed.expires,
            signature: parsed.signature
        )
        return IosBytesKt.nsDataOf(bytes: bytes) as Data
    }

    /// What the slip is sent as. A screenshot picked from the library is a JPEG or a PNG and the
    /// gateway sniffs it either way; the part still needs a media type, and this is the honest one.
    private static let slipContentType = "image/*"
}
