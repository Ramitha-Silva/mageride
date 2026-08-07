import Combine
import Foundation

/// SCR-DI-034's state.
///
/// - Parameters:
///   - alerts: Every stored push, newest first.
///   - isLoading: The first read is in flight — the wireframe's shimmer.
///   - opening: A destination one row asked for, consumed by the screen.
struct NotificationsState {

    var alerts: [DriverAlert] = []
    var isLoading = true
    var opening: DriverRoute?

    /// Whether the list is genuinely empty rather than not yet read.
    var isEmpty: Bool { !isLoading && alerts.isEmpty }

    /// Whether **Mark all read** is offered. Nothing to mark is nothing to draw.
    var hasUnread: Bool { alerts.contains { !$0.isRead } }
}

/// **SCR-DI-034 · alerts** (Epic 10, D5' §14.4).
///
/// **Read from the device, not from the platform.** There is no *"list my notifications"* operation
/// on the app-facing surface — see ``NotificationInbox`` — so this is `mobile_db_schema.md` §1.6,
/// which is also why the screen works with no connection at all.
///
/// **A row's deep link is resolved, never trusted** (the shell's rule). The stored `deeplink` came
/// over the network inside an APNs payload; ``PushRouter/resolve(_:)`` maps it onto a known
/// ``DriverRoute`` and an unrecognised one opens nothing rather than being handed to the navigator.
@MainActor
final class NotificationsModel: ObservableObject {

    @Published private(set) var state = NotificationsState()

    private let inbox: NotificationInbox

    init(inbox: NotificationInbox) {
        self.inbox = inbox
    }

    /// Re-reads §1.6.
    func refresh() async {
        state.isLoading = true
        state.alerts = await inbox.all()
        state.isLoading = false
    }

    /// Opens one alert.
    ///
    /// Marked read **locally and immediately** — the row is the device's own and there is nothing to
    /// confirm — and then routed if its link resolves to a screen. An alert that opens nothing is
    /// still marked read: the driver has looked at it, which is the only thing `read` claims.
    func open(_ alert: DriverAlert) {
        if let index = state.alerts.firstIndex(where: { $0.id == alert.id }) {
            state.alerts[index].isRead = true
        }
        state.opening = PushRouter.resolve(alert.deeplink)

        Task { [inbox] in await inbox.markRead(id: alert.id) }
    }

    /// Clears the pending navigation once the screen has acted on it.
    func consumeOpening() {
        state.opening = nil
    }

    /// Marks the whole list read.
    func markAllRead() {
        state.alerts = state.alerts.map { alert in
            var marked = alert
            marked.isRead = true
            return marked
        }
        Task { [inbox] in await inbox.markAllRead() }
    }
}
