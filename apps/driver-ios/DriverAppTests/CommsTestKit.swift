import Foundation
import MageRideShared

@testable import DriverApp

/// The fakes and fixtures cluster 8's models are driven by.
///
/// Same rule as every other test kit here: each seam is a **Swift protocol**, because the Kotlin
/// types behind it are classes and interfaces Swift cannot stand in for, and the DTOs underneath are
/// the real shared ones — a fixture is built with the initialiser the gateway's response
/// deserialises into, so a contract change fails these tests rather than a driver's phone.

// `testTripId`, `tripSummary(…)` and `emergencyContact(…)` are C092's and are in `ProfileTestKit` —
// SCR-DI-033a's **Related trip** picker reads the same `GET /v1/trips/{driverId}` SCR-DI-030 does,
// and SCR-DI-032 reads the same emergency contact SCR-DI-029 writes. Two fixtures for one endpoint
// is how two screens end up disagreeing about a shape the gateway only has one of.

let testCallId = "01JCALL0000000000000000001"
let testTicketId = "01JTICKET00000000000000001"
let testArticleId = "01JFAQ00000000000000000001"

/// `POST /v1/calls/start` — 200, with the session a free call gets and a direct dial does not.
func startCallResponse(session: VoipSession? = testVoipSession) -> StartCallResponse {
    StartCallResponse(callId: testCallId, callType: CallType.freeVoip, session: session)
}

let testVoipSession = VoipSession(
    roomName: "ride_\(testRideId)",
    token: "voip.jwt.token",
    wsUrl: "wss://voip.mageride.lk"
)

/// One support ticket as `GET /v1/support/tickets/{userId}` lists it.
func supportTicket(
    ticketId: String = testTicketId,
    category: String = SupportCategories.general,
    status: TicketStatus = TicketStatus.open
) -> Ticket {
    Ticket(
        ticketId: ticketId,
        category: category,
        status: status,
        queue: category == SupportCategories.dailyFeeRefund ? TicketQueue.finance : TicketQueue.support,
        tripId: nil,
        createdAt: IosInstantKt.timestampFromEpochMillis(millis: 0),
        updatedAt: nil,
        resolvedAt: nil
    )
}

func faqSummary(articleId: String = testArticleId, title: String = "How the daily fee works") -> FaqSummary {
    FaqSummary(articleId: articleId, title: title, category: "wallet", language: Language.en)
}

// MARK: - The seams

/// ``VoipEngine`` with no media at all, whose link the test drives by hand.
///
/// The production binding is ``AbsentVoipEngine`` and it fails immediately; this one holds the
/// handler so a test can reach ``CallStage/connected`` — the state a build with no WebRTC client
/// cannot otherwise get to, and the one the timer and the outcome log hang on.
@MainActor
final class FakeVoipEngine: VoipEngine {

    /// Emitted the moment ``join(session:onLink:)`` is called, if set.
    var linkOnJoin: CallLink?

    private(set) var joined: [VoipSession] = []
    private(set) var mutes: [Bool] = []
    private(set) var speakers: [Bool] = []
    private(set) var leaveCount = 0

    private var onLink: ((CallLink) -> Void)?

    func join(session: VoipSession, onLink: @escaping (CallLink) -> Void) {
        joined.append(session)
        self.onLink = onLink
        if let linkOnJoin { onLink(linkOnJoin) }
    }

    /// What a real engine's connection callback would do.
    func emit(_ link: CallLink) {
        onLink?(link)
    }

    func setMicrophoneMuted(_ muted: Bool) { mutes.append(muted) }

    func setSpeakerphoneOn(_ on: Bool) { speakers.append(on) }

    func leave() { leaveCount += 1 }
}

/// ``CallSession`` with no CallKit. A test host has no `CXProvider` worth reporting to.
@MainActor
final class FakeCallSession: CallSession {

    var onSystemEnd: (() -> Void)?
    var onSystemMute: ((Bool) -> Void)?

    private(set) var connectingHandles: [String] = []
    private(set) var connectedCount = 0
    private(set) var ends: [CallEndReason] = []
    private(set) var mutes: [Bool] = []

    func startedConnecting(handle: String) { connectingHandles.append(handle) }

    func connected() { connectedCount += 1 }

    func end(reason: CallEndReason) { ends.append(reason) }

    func setMuted(_ muted: Bool) { mutes.append(muted) }
}

/// ``SupportRepository`` with no support-svc.
@MainActor
final class FakeSupportRepository: SupportRepository {

    var articles: [FaqSummary] = []
    var storedTickets: [Ticket] = []
    var storedTrips: [TripSummary] = []
    var articleToReturn = FaqArticle(
        articleId: testArticleId,
        title: "How the daily fee works",
        category: "wallet",
        language: Language.en,
        body: "# Daily fee\nRs 100 from the second trip."
    )
    var ticketToReturn: TicketDetail?
    var uploadedFileId = "01JUPLOAD00000000000000001"

    var faqFailure: Error?
    var ticketsFailure: Error?
    var detailFailure: Error?
    var uploadFailure: Error?
    var raiseFailure: Error?

    private(set) var faqReads = 0
    private(set) var ticketReads = 0
    private(set) var tripReads = 0
    private(set) var uploads: [CapturedImage] = []
    private(set) var raised: [(category: String, description: String, tripId: String?, fileId: String?)] = []

    func faq() async throws -> [FaqSummary] {
        faqReads += 1
        if let faqFailure { throw faqFailure }
        return articles
    }

    func article(articleId: String) async throws -> FaqArticle {
        if let detailFailure { throw detailFailure }
        return articleToReturn
    }

    func tickets(userId: String) async throws -> [Ticket] {
        ticketReads += 1
        if let ticketsFailure { throw ticketsFailure }
        return storedTickets
    }

    func ticket(userId: String, ticketId: String) async throws -> TicketDetail {
        if let detailFailure { throw detailFailure }
        guard let ticketToReturn else { throw CancellationError() }
        return ticketToReturn
    }

    func trips(driverId: String) async throws -> [TripSummary] {
        tripReads += 1
        return storedTrips
    }

    func uploadScreenshot(_ image: CapturedImage) async throws -> String {
        uploads.append(image)
        if let uploadFailure { throw uploadFailure }
        return uploadedFileId
    }

    func raise(
        category: String,
        description: String,
        tripId: String?,
        screenshotFileId: String?
    ) async throws -> Ticket {
        raised.append((category, description, tripId, screenshotFileId))
        if let raiseFailure { throw raiseFailure }
        return supportTicket(category: category)
    }
}

/// ``NotificationInbox`` with no SQLite. `mobile_db_schema.md` §1.6, in memory.
final class FakeNotificationInbox: NotificationInbox, @unchecked Sendable {

    var stored: [DriverAlert] = []

    private(set) var recorded: [(message: PushMessage, title: String?, body: String?)] = []
    private(set) var readIds: [String] = []
    private(set) var markAllCount = 0

    func record(_ message: PushMessage, title: String?, body: String?) async {
        recorded.append((message, title, body))
    }

    func all() async -> [DriverAlert] { stored }

    func markRead(id: String) async {
        readIds.append(id)
        stored = stored.map { alert in
            guard alert.id == id else { return alert }
            var marked = alert
            marked.isRead = true
            return marked
        }
    }

    func markAllRead() async {
        markAllCount += 1
        stored = stored.map { alert in
            var marked = alert
            marked.isRead = true
            return marked
        }
    }
}

/// One stored alert.
func driverAlert(
    id: String = "alert-1",
    type: String = "LOW_BALANCE",
    title: String? = "Low balance — top up",
    body: String? = nil,
    deeplink: String? = nil,
    isRead: Bool = false,
    ageSeconds: TimeInterval = 60
) -> DriverAlert {
    DriverAlert(
        id: id,
        type: type,
        title: title,
        body: body,
        deeplink: deeplink,
        isRead: isRead,
        receivedAt: Date().addingTimeInterval(-ageSeconds)
    )
}
