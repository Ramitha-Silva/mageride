import Foundation
import MageRideShared

@testable import PassengerApp

/// Cluster 8's four seams, faked, plus the fixtures its suite is written against.
///
/// Same rule as ``OnboardingTestKit``, ``HomeTestKit``, ``BookingTestKit``, ``RideTestKit``,
/// ``HistoryTestKit``, ``SubscriptionTestKit`` and ``SettingsTestKit``: **these stand in for Swift
/// protocols, never for Kotlin classes.** ``SafetyRepository`` and ``SupportRepository`` exist
/// precisely because `SafetyApi` and `SupportApi` are Kotlin interfaces with `suspend` methods that
/// Swift cannot implement; ``VoipEngine`` and ``CallSession`` exist because a LiveKit room needs a
/// radio and `CXProvider` needs a CallKit-capable process, and a model test has neither.
///
/// The ride half is ``FakeRideRepository``'s, which cluster 4 already owns and C102 extended with
/// `reportCallOutcome` rather than forking — same argument as ``RideRepository`` itself having one
/// implementation across both clusters.

// MARK: - The WebRTC seam

/// ``VoipEngine`` with the link under a test's control.
///
/// **Not ``AbsentVoipEngine``.** That one is what the *app* binds and it fails immediately, which
/// makes exactly one of SCR-PI-028's three states reachable; this one starts wherever the test says
/// and can be moved afterwards, which is how the connected state and its timer get asserted at all.
@MainActor
final class FakeVoipEngine: VoipEngine {

    /// What ``join(session:onLink:)`` reports the moment it is called. `nil` reports nothing, which
    /// is a room that is still being joined.
    var linkOnJoin: CallLink? = .connecting

    private(set) var joined: [VoipSession] = []
    private(set) var muted: [Bool] = []
    private(set) var speaker: [Bool] = []
    private(set) var leaves = 0

    private var handler: ((CallLink) -> Void)?

    func join(session: VoipSession, onLink: @escaping (CallLink) -> Void) {
        joined.append(session)
        handler = onLink
        if let linkOnJoin { onLink(linkOnJoin) }
    }

    /// Moves the link after the join — a room that connected, or one that fell over.
    func report(_ link: CallLink) {
        handler?(link)
    }

    func setMicrophoneMuted(_ muted: Bool) {
        self.muted.append(muted)
    }

    func setSpeakerphoneOn(_ on: Bool) {
        speaker.append(on)
    }

    func leave() {
        leaves += 1
    }
}

// MARK: - CallKit

/// ``CallSession`` as a recorder, so *when* the system was told about the call is assertable.
///
/// The ordering is the whole point of the type existing: a call reported before the room connected
/// puts a system call in the status bar for one that never happened, and a reported call left up
/// when AL-48's fallback is offered makes the `tel:` dial hang itself up.
@MainActor
final class FakeCallSession: CallSession {

    var onSystemEnd: (() -> Void)?
    var onSystemMute: ((Bool) -> Void)?

    private(set) var connectingHandles: [String] = []
    private(set) var connectedCount = 0
    private(set) var ended: [CallEndReason] = []
    private(set) var muted: [Bool] = []

    /// Every report in order, as one list, so a test can assert the *sequence* rather than four
    /// counters that could each be right while the order is wrong.
    private(set) var log: [CallReport] = []

    func startedConnecting(handle: String) {
        connectingHandles.append(handle)
        log.append(.startedConnecting)
    }

    func connected() {
        connectedCount += 1
        log.append(.connected)
    }

    func end(reason: CallEndReason) {
        ended.append(reason)
        log.append(reason == .failed ? .endedFailed : .endedLocally)
    }

    func setMuted(_ muted: Bool) {
        self.muted.append(muted)
    }
}

/// One thing the app told CallKit. `Equatable` so a whole sequence is one assertion.
enum CallReport: Equatable {
    case startedConnecting
    case connected
    case endedLocally
    case endedFailed
}

// MARK: - safety-svc

final class FakeSafetyRepository: SafetyRepository, @unchecked Sendable {

    var dispatched = SafetyFixtures.dispatched()
    var link = SafetyFixtures.shareLink

    /// Thrown by the **next** `POST /v1/sos`, then cleared. One-shot, so a suite can arm a failure
    /// and then assert that the retry succeeds.
    var sosFailure: Error?

    /// Thrown by every `POST /v1/trip-share/{tripId}` while it is set, and **not** cleared: D-34's
    /// link is best-effort and a test asserting that has to keep it failing while the alarm succeeds.
    var shareFailure: Error?

    private(set) var raised: [RaisedSos] = []
    private(set) var shared: [String] = []

    func triggerSos(rideId: String, lat: Double, lng: Double) async throws -> SosDispatched {
        raised.append(RaisedSos(rideId: rideId, lat: lat, lng: lng))
        if let sosFailure {
            self.sosFailure = nil
            throw sosFailure
        }
        return dispatched
    }

    func shareTrip(rideId: String) async throws -> TripShareLink {
        shared.append(rideId)
        if let shareFailure { throw shareFailure }
        return link
    }
}

/// One `POST /v1/sos`, recorded.
///
/// A named type for ``ReplacedAddress``' reason — `raised.map(\.lat)` is a key path, and **Swift has
/// none into a tuple** (the C087 finding).
struct RaisedSos {
    let rideId: String
    let lat: Double
    let lng: Double
}

// MARK: - support-svc

final class FakeSupportRepository: SupportRepository, @unchecked Sendable {

    var articles: [FaqSummary] = []
    var article = SupportFixtures.article()
    var storedTickets: [Ticket] = []
    var detail = SupportFixtures.detail()
    var storedTrips: [RideHistoryRow] = []
    var fileId = SupportFixtures.fileId

    /// Thrown by every FAQ list read while set — the failure that must leave the *tickets* alone.
    var faqFailure: Error?

    /// Thrown by every ticket list read while set — the failure that must leave the *FAQ* alone.
    var ticketsFailure: Error?

    var articleFailure: Error?
    var detailFailure: Error?
    var uploadFailure: Error?
    var raiseFailure: Error?

    private(set) var faqLanguages: [Language?] = []
    private(set) var articleReads: [ReadArticle] = []
    private(set) var ticketReads: [String] = []
    private(set) var tripReads = 0
    private(set) var uploads: [UploadedFile] = []
    private(set) var raisedTickets: [RaisedTicket] = []

    func faq(language: Language?) async throws -> [FaqSummary] {
        faqLanguages.append(language)
        if let faqFailure { throw faqFailure }
        return articles
    }

    func article(articleId: String, language: Language?) async throws -> FaqArticle {
        articleReads.append(ReadArticle(articleId: articleId, language: language))
        if let articleFailure { throw articleFailure }
        return article
    }

    func tickets(userId: String) async throws -> [Ticket] {
        ticketReads.append(userId)
        if let ticketsFailure { throw ticketsFailure }
        return storedTickets
    }

    func ticket(userId: String, ticketId: String) async throws -> TicketDetail {
        if let detailFailure { throw detailFailure }
        return detail
    }

    func trips() async throws -> [RideHistoryRow] {
        tripReads += 1
        return storedTrips
    }

    func uploadScreenshot(fileName: String, data: Data) async throws -> String {
        uploads.append(UploadedFile(fileName: fileName, byteCount: data.count))
        if let uploadFailure { throw uploadFailure }
        return fileId
    }

    func raise(
        category: String,
        description: String,
        tripId: String?,
        screenshotFileId: String?
    ) async throws -> Ticket {
        raisedTickets.append(
            RaisedTicket(
                category: category,
                description: description,
                tripId: tripId,
                screenshotFileId: screenshotFileId
            )
        )
        if let raiseFailure { throw raiseFailure }
        return SupportFixtures.ticket(
            ticketId: SupportFixtures.raisedTicketId,
            category: category,
            tripId: tripId
        )
    }
}

/// One `GET /v1/support/faq/{articleId}`, recorded — the language is what AL-26 is asserted on.
struct ReadArticle {
    let articleId: String
    let language: Language?
}

/// One `POST /v1/support/screenshots`. The **count** rather than the bytes: an assertion on two
/// megabytes of `Data` says nothing a length does not.
struct UploadedFile {
    let fileName: String
    let byteCount: Int
}

/// One `POST /v1/support/tickets`, recorded.
struct RaisedTicket {
    let category: String
    let description: String
    let tripId: String?
    let screenshotFileId: String?
}

// MARK: -

/// The canonical values SCR-PI-029's suite is written against.
enum SafetyFixtures {

    static let sosId = "01JSOS00000000000000000A01"
    static let shareToken = "sh_9f2c41ab"

    /// Where the passenger is when the alarm goes — Galle Face, the same point cluster 2 uses.
    static let fix = PassengerFix(lat: 6.9344, lng: 79.8428, accuracyMetres: 12)

    static let shareLink = TripShareLink(
        token: shareToken,
        url: "https://mageride.lk/t/\(shareToken)",
        expiresAt: HistoryFixtures.timestamp()
    )

    static func dispatched(smsStatus: SosSmsStatus = SosSmsStatus.dispatched) -> SosDispatched {
        SosDispatched(sosId: sosId, dispatchedAt: HistoryFixtures.timestamp(), smsStatus: smsStatus)
    }
}

/// The canonical values SCR-PI-030's and SCR-PI-030a's suites are written against.
enum SupportFixtures {

    static let receiptArticleId = "01JFAQ00000000000000000001"
    static let paymentArticleId = "01JFAQ00000000000000000002"
    static let ticketId = "01JTKT00000000000000000001"
    static let raisedTicketId = "01JTKT00000000000000000009"
    static let fileId = "01JUPL00000000000000000001"

    /// The wireframe's own two rows.
    static func summaries() -> [FaqSummary] {
        [
            summary(articleId: receiptArticleId, title: "How do I get a receipt?"),
            summary(articleId: paymentArticleId, title: "Payment failed — what now?"),
        ]
    }

    static func summary(
        articleId: String,
        title: String,
        language: Language = Language.en
    ) -> FaqSummary {
        FaqSummary(articleId: articleId, title: title, category: "billing", language: language)
    }

    static func article(
        articleId: String = receiptArticleId,
        body: String = "Open the trip in Trips and tap Receipt.",
        language: Language = Language.en
    ) -> FaqArticle {
        FaqArticle(
            articleId: articleId,
            title: "How do I get a receipt?",
            category: "billing",
            language: language,
            body: body
        )
    }

    static func ticket(
        ticketId: String = ticketId,
        category: String = SupportCategories.general,
        status: TicketStatus = TicketStatus.open,
        tripId: String? = nil
    ) -> Ticket {
        Ticket(
            ticketId: ticketId,
            category: category,
            status: status,
            queue: TicketQueue.support,
            tripId: tripId,
            createdAt: HistoryFixtures.timestamp(),
            updatedAt: nil,
            resolvedAt: nil
        )
    }

    /// One ticket with a thread that includes the `assigned` entry the screen must skip.
    static func detail(
        status: TicketStatus = TicketStatus.inProgress,
        description: String = "Charged twice for one trip"
    ) -> TicketDetail {
        TicketDetail(
            ticketId: ticketId,
            category: SupportCategories.general,
            status: status,
            queue: TicketQueue.support,
            tripId: nil,
            createdAt: HistoryFixtures.timestamp(),
            updatedAt: nil,
            resolvedAt: nil,
            description: description,
            screenshotUrl: nil,
            adminResponse: nil,
            thread: [
                event(kind: TicketEventKind.opened),
                event(kind: TicketEventKind.assigned),
                event(kind: TicketEventKind.responded, body: "We are looking into it."),
            ]
        )
    }

    static func event(kind: TicketEventKind, body: String? = nil) -> TicketEvent {
        TicketEvent(
            kind: kind,
            at: HistoryFixtures.timestamp(),
            fromStatus: nil,
            toStatus: nil,
            body: body,
            actorRole: nil
        )
    }
}
