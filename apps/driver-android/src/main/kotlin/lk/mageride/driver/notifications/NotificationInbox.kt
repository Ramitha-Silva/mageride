package lk.mageride.driver.notifications

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import lk.mageride.driver.di.DriverDatabase
import lk.mageride.driver.push.PushMessage
import lk.mageride.shared.db.driver.Notifications
import lk.mageride.shared.serialization.MageRideJson
import java.util.UUID
import kotlin.time.Clock
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * The local push table — `mobile_db_schema.md` §1.6, *"local push inbox (Epic 10, E-01)"*.
 *
 * **SCR-DA-034 is drawn from what arrived on this handset, not from a server read.** There is no
 * *"list my notifications"* operation anywhere on the app-facing surface: `notification.yaml` mints,
 * registers a token, sets preferences and acknowledges, and §1.6 is the read model the schema
 * document provides instead. So the alert list is exactly what FCM delivered while the app was
 * installed — which is also why it survives being offline, which is what the DoD asks of it.
 *
 * **Two writers, both cheap.** `DriverMessagingService` records every push as it arrives (including
 * the ones it does not post a tray notification for), and the screen marks rows read. §4.3's sweep
 * keeps 30 days or the last 200, whichever bites first, and `DriverDb`'s retention already runs it.
 *
 * An interface with one production implementation, for the reason `ActiveVehicleStore` and
 * `TrackerBindingStore` are: the real one opens an encrypted SQLite file through an Android driver,
 * and a view model tested against it on this host would be testing a stub whose every member throws.
 */
internal interface NotificationInbox {

    /** Records one push. */
    suspend fun record(push: PushMessage, title: String?, body: String?)

    /** Every alert, newest first — the wireframe's list. */
    suspend fun all(): List<DriverAlert>

    /** Marks one row read. Idempotent; an id nobody has is a no-op, not an error. */
    suspend fun markRead(id: String)

    /** Marks everything read. */
    suspend fun markAllRead()
}

/**
 * [NotificationInbox] over `mageride_driver.db`.
 *
 * Every call hops to [Dispatchers.IO]: SQLDelight's Android driver is synchronous and this is
 * called from a `FirebaseMessagingService` callback and from a view model.
 */
@OptIn(ExperimentalTime::class)
internal class LocalNotificationInbox(
    private val databases: DriverDatabase,
    private val clock: () -> Instant = { Clock.System.now() },
) : NotificationInbox {

    /**
     * Records one push.
     *
     * `INSERT OR REPLACE` on the server's own `notificationId`, so a push redelivered by FCM (which
     * it will be — at-least-once is the guarantee) updates the row it already wrote rather than
     * filling the inbox with duplicates. A push with **no** id gets a client UUID: §1.6's own column
     * comment allows for one, and a notification with no identity is still one the driver saw.
     *
     * @param title What the tray showed, when the push carried one.
     * @param body The tray's second line.
     */
    override suspend fun record(push: PushMessage, title: String?, body: String?): Unit = withContext(Dispatchers.IO) {
        val database = databases.get()
        database.sql.notificationsQueries.upsert(
            id = push.notificationId ?: UUID.randomUUID().toString(),
            type = push.kind ?: UNKNOWN_TYPE,
            title = title,
            body = body,
            data_json = encode(push.data),
            ride_id = push.data[KEY_RIDE_ID],
            read = false,
            received_at = clock(),
        )
    }

    override suspend fun all(): List<DriverAlert> = withContext(Dispatchers.IO) {
        databases.get().sql.notificationsQueries.selectAll().executeAsList().map(Notifications::toAlert)
    }

    override suspend fun markRead(id: String): Unit = withContext(Dispatchers.IO) {
        databases.get().sql.notificationsQueries.markRead(id)
    }

    /**
     * Row by row rather than by one `UPDATE`, because §1.6 declares no such statement and a
     * migration to add one belongs to whoever owns `mobile_db_schema.md`. The list is capped at 200
     * by the §4.3 sweep, so the loop is bounded by the schema rather than by hope, and it runs in
     * one transaction.
     */
    override suspend fun markAllRead(): Unit = withContext(Dispatchers.IO) {
        val database = databases.get()
        database.transaction {
            database.sql.notificationsQueries.selectUnread().executeAsList().forEach {
                database.sql.notificationsQueries.markRead(it.id)
            }
        }
    }

    private fun encode(data: Map<String, String>): String? = data
        .takeIf { it.isNotEmpty() }
        ?.let { MageRideJson.encodeToString(PUSH_DATA, it) }

    private companion object {

        /** What a push with no `kind` is filed as. Resolves to the neutral row (see `AlertKind`). */
        const val UNKNOWN_TYPE = "UNKNOWN"

        /** §1.6's `ride_id` column, from the push's own data dictionary. */
        const val KEY_RIDE_ID = "rideId"
    }
}

/**
 * One row of SCR-DA-034.
 *
 * A view type rather than the generated row: the screen needs the deep link back out of
 * `data_json`, and a composable that decoded JSON per frame would do it on every recomposition.
 *
 * @property id §1.6's primary key — the server's notification id, or a client UUID.
 * @property type `data.kind`, which is what [AlertKind] resolves the icon and tone from.
 * @property title The tray's headline, when the push carried one.
 * @property body Its second line.
 * @property deeplink The `mageride://…` URI, still unresolved — `PushRouter` is what maps it.
 * @property read Whether the driver has opened it.
 * @property receivedAt When this handset saw it.
 */
internal data class DriverAlert(
    val id: String,
    val type: String,
    val title: String?,
    val body: String?,
    val deeplink: String?,
    val read: Boolean,
    val receivedAt: Instant,
)

/**
 * The stored row as the screen reads it.
 *
 * `data_json` is decoded **once**, here, rather than in a composable that would parse it on every
 * recomposition. A row written by an older build, or one whose payload was truncated, yields no
 * deep link rather than throwing: the alert is still worth showing, it just does not open anything.
 */
@OptIn(ExperimentalTime::class)
private fun Notifications.toAlert(): DriverAlert = DriverAlert(
    id = id,
    type = type,
    title = title,
    body = body,
    deeplink = data_json?.let { json ->
        runCatching { MageRideJson.decodeFromString(PUSH_DATA, json)[PushMessage.KEY_DEEPLINK] }.getOrNull()
    },
    read = read,
    receivedAt = received_at,
)

/** FCM's data payload is `Map<String, String>` on the wire, and §1.6 stores it verbatim. */
private val PUSH_DATA = MapSerializer(String.serializer(), String.serializer())
