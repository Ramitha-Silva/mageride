import Foundation
import MageRideShared

/// One row of SCR-DI-034.
///
/// A view type rather than `:shared`'s ``IosStoredAlert``: the screen wants a `Date` and this app's
/// own idea of identity, and a value that crossed the bridge is not the value a `ForEach` should be
/// keyed on. The conversion is one initialiser and it happens once per read.
///
/// - Parameters:
///   - id: §1.6's primary key — the server's notification id, or a client UUID.
///   - type: `data.kind`, which is what ``AlertKind`` resolves the icon and tone from.
///   - title: The tray's headline, when the push carried one.
///   - body: Its second line.
///   - deeplink: The `mageride://…` URI, still unresolved — ``PushRouter`` is what maps it.
///   - isRead: Whether the driver has opened it.
///   - receivedAt: When this handset saw it.
struct DriverAlert: Equatable, Identifiable {

    let id: String
    let type: String
    let title: String?
    let body: String?
    let deeplink: String?
    var isRead: Bool
    let receivedAt: Date

    init(stored: IosStoredAlert) {
        id = stored.id
        type = stored.type
        title = stored.title
        body = stored.body
        deeplink = stored.deeplink
        isRead = stored.read
        receivedAt = Date(timeIntervalSince1970: TimeInterval(stored.receivedAtMillis) / 1000)
    }

    init(
        id: String,
        type: String,
        title: String? = nil,
        body: String? = nil,
        deeplink: String? = nil,
        isRead: Bool = false,
        receivedAt: Date
    ) {
        self.id = id
        self.type = type
        self.title = title
        self.body = body
        self.deeplink = deeplink
        self.isRead = isRead
        self.receivedAt = receivedAt
    }
}

/// The local push table — `mobile_db_schema.md` §1.6, *"local push inbox (Epic 10, E-01)"*.
///
/// **SCR-DI-034 is drawn from what arrived on this handset, not from a server read.** There is no
/// *"list my notifications"* operation anywhere on the app-facing surface: `notification.yaml` mints,
/// registers a token, sets preferences and acknowledges, and §1.6 is the read model the schema
/// document provides instead. So the alert list is exactly what APNs delivered while the app was
/// installed — which is also why it works with no connection, which is what the DoD asks of it.
///
/// **Δ iOS — what "every push" can mean here is narrower than on Android, and the gap is APNs'.**
/// `FirebaseMessagingService.onMessageReceived` fires for every data message the handset receives;
/// iOS hands a push to the app in three cases and only three — it is presented in the foreground, it
/// is tapped, or it carries `content-available` and the system chooses to wake the app. All three
/// reach ``DriverAppDelegate`` and all three are filed. A `content-available` push the system
/// declines to deliver (low power, a heavy budget) is simply never seen, and no local inbox on this
/// platform can do better. Recorded as a Section C difference rather than worked around.
///
/// **Two writers, both cheap.** The push delegate records every push as it arrives, and the screen
/// marks rows read. §4.3's sweep keeps 30 days or the last 200, whichever bites first.
///
/// A protocol with one production implementation, for the reason ``ActiveVehicleStore`` and
/// ``TrackerBindingStore`` are: the real one opens an encrypted SQLite file through a Native driver,
/// and a model tested against it on a build host would be testing a stub whose every member throws.
protocol NotificationInbox: AnyObject, Sendable {

    /// Records one push.
    func record(_ message: PushMessage, title: String?, body: String?) async

    /// Every alert, newest first — the wireframe's list.
    func all() async -> [DriverAlert]

    /// Marks one row read. Idempotent; an id nobody has is a no-op, not an error.
    func markRead(id: String) async

    /// Marks everything read.
    func markAllRead() async
}

/// ``NotificationInbox`` over `mageride_driver.db`.
///
/// An `actor` for ``DriverDatabase``'s reason and one more: every call underneath is **blocking**
/// (SQLDelight's Native driver is synchronous), and this one is reached from a push delegate that
/// runs on the main thread. An actor is where that work goes off it.
actor LocalNotificationInbox: NotificationInbox {

    private let databases: DriverDatabase
    private let now: () -> Date

    init(databases: DriverDatabase, now: @escaping () -> Date = Date.init) {
        self.databases = databases
        self.now = now
    }

    func record(_ message: PushMessage, title: String?, body: String?) async {
        guard let database = await databases.get() else { return }
        IosNotificationInboxKt.recordNotification(
            database: database,
            // A push with no id still gets a row: §1.6's own column comment allows a client UUID,
            // and a notification the driver saw is one they can be shown again.
            id: message.notificationId ?? UUID().uuidString,
            type: message.kind ?? Self.unknownType,
            title: title,
            body: body,
            data: message.data,
            receivedAtMillis: Int64(now().timeIntervalSince1970 * 1000)
        )
    }

    func all() async -> [DriverAlert] {
        guard let database = await databases.get() else { return [] }
        return IosNotificationInboxKt.readNotifications(database: database).map(DriverAlert.init(stored:))
    }

    func markRead(id: String) async {
        guard let database = await databases.get() else { return }
        IosNotificationInboxKt.markNotificationRead(database: database, id: id)
    }

    func markAllRead() async {
        guard let database = await databases.get() else { return }
        IosNotificationInboxKt.markAllNotificationsRead(database: database)
    }

    /// What a push with no `kind` is filed as. Resolves to the neutral row — see ``AlertKind``.
    private static let unknownType = "UNKNOWN"
}
