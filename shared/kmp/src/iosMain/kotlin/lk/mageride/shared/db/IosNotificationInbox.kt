// The file is §1.6's four operations plus the row they answer with, and it is named after the
// operations: naming it `IosStoredAlert.kt` would file the inbox under its return type, where
// nobody looking for "where does iOS read the notification table" would find it. Same shape as
// `IosPositionPipeline.kt`, which is also a set of functions over one carried value.
@file:Suppress("MatchingDeclarationName")

package lk.mageride.shared.db

import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import lk.mageride.shared.db.driver.DriverDb
import lk.mageride.shared.serialization.MageRideJson
import kotlin.time.Instant

/**
 * One row of `mobile_db_schema.md` §1.6, as Swift reads it (C093).
 *
 * ### Why this file exists at all
 *
 * The **generated SQLDelight query types do not belong on the bridge.**
 * `DriverDb.sql.notificationsQueries.selectAll()` answers an
 * `app.cash.sqldelight.Query<Notifications>`, and `Query` lives in the SQLDelight runtime — a
 * dependency the framework does not `export`. A Swift caller would be reaching through
 * implicitly-exported types from another module, over a generated row class, to run a query whose
 * every call is blocking. `PositionService` already avoids exactly this by taking [GpsBuffer], which
 * is `:shared`'s own type; this is the same door for §1.6.
 *
 * The second reason is the one every other `iosMain` helper here gives: `data_json` is
 * `kotlinx.serialization`'s and the deep link inside it has to be decoded **once**, on read, rather
 * than in a view that would parse JSON on every redraw. Kotlin owns the codec, so Kotlin does it.
 *
 * @property id §1.6's primary key — the server's notification id, or a client UUID.
 * @property type `data.kind`, which is what the app's `AlertKind` resolves the icon and tone from.
 * @property title The tray's headline, when the push carried one.
 * @property body Its second line.
 * @property deeplink The `mageride://…` URI, still **unresolved** — `PushRouter` is what maps it.
 * @property read Whether the driver has opened it.
 * @property receivedAtMillis When this handset saw it, in epoch milliseconds (§0.3). Milliseconds
 *   rather than an [Instant] because that type's Swift-facing surface is an implementation detail of
 *   the compiler, and every other timestamp this app takes across the bridge is a `Long` for the
 *   same reason (see `IosInstant.kt`).
 */
public data class IosStoredAlert(
    val id: String,
    val type: String,
    val title: String?,
    val body: String?,
    val deeplink: String?,
    val read: Boolean,
    val receivedAtMillis: Long,
)

// Top-level functions taking the database as a parameter rather than extensions on [DriverDb],
// which is the shape every other `iosMain` helper in this module has (`rideProjectionCanSend`,
// `walletAlertFor`, `jobBoardGoesLiveAtMillis`). A Kotlin extension's exported Swift spelling —
// category method or file-class static — is a compiler detail, and this host cannot link a
// framework to find out which one it chose.

/**
 * Records one push (§1.6).
 *
 * `INSERT OR REPLACE` on the server's own notification id, so a push redelivered by APNs — which it
 * will be; at-least-once is the guarantee — updates the row it already wrote rather than filling the
 * inbox with duplicates. A push with **no** id gets whatever the caller minted: §1.6's own column
 * comment allows for a client UUID, and a notification with no identity is still one the driver saw.
 *
 * @param database The open driver database.
 * @param id The server's `notificationId`, or a client-minted one.
 * @param type `data.kind`, or a placeholder for a push that carried none.
 * @param title What the tray showed.
 * @param body The tray's second line.
 * @param data The push's whole data dictionary, stored verbatim.
 * @param receivedAtMillis Now, in epoch milliseconds.
 */
@Suppress("LongParameterList") // One parameter per §1.6 column the app supplies; a holder type would
// be `IosStoredAlert` without its `read` and its deep link, which is a second row shape for one table.
public fun recordNotification(
    database: DriverDb,
    id: String,
    type: String,
    title: String?,
    body: String?,
    data: Map<String, String>,
    receivedAtMillis: Long,
) {
    database.sql.notificationsQueries.upsert(
        id = id,
        type = type,
        title = title,
        body = body,
        data_json = data.takeIf { it.isNotEmpty() }?.let { MageRideJson.encodeToString(PUSH_DATA, it) },
        ride_id = data[KEY_RIDE_ID],
        read = false,
        received_at = Instant.fromEpochMilliseconds(receivedAtMillis),
    )
}

/** Every alert, newest first — SCR-DI-034's list. */
public fun readNotifications(database: DriverDb): List<IosStoredAlert> =
    database.sql.notificationsQueries.selectAll().executeAsList().map { row ->
        IosStoredAlert(
            id = row.id,
            type = row.type,
            title = row.title,
            body = row.body,
            // Decoded once, here. A row written by an older build, or one whose payload was
            // truncated, yields no deep link rather than throwing: the alert is still worth showing,
            // it just does not open anything.
            deeplink = row.data_json?.let {
                runCatching { MageRideJson.decodeFromString(PUSH_DATA, it)[KEY_DEEPLINK] }.getOrNull()
            },
            read = row.read,
            receivedAtMillis = row.received_at.toEpochMilliseconds(),
        )
    }

/** Marks one row read. Idempotent; an id nobody has is a no-op, not an error. */
public fun markNotificationRead(database: DriverDb, id: String) {
    database.sql.notificationsQueries.markRead(id)
}

/**
 * Marks everything read.
 *
 * Row by row rather than by one `UPDATE`, because §1.6 declares no such statement and a migration to
 * add one belongs to whoever owns `mobile_db_schema.md`. The list is capped at 200 by the §4.3
 * sweep, so the loop is bounded by the schema rather than by hope, and it runs in one transaction.
 */
public fun markAllNotificationsRead(database: DriverDb) {
    database.transaction {
        database.sql.notificationsQueries.selectUnread().executeAsList().forEach {
            database.sql.notificationsQueries.markRead(it.id)
        }
    }
}

/** §1.6's `ride_id` column, from notification-svc's own push data dictionary. */
private const val KEY_RIDE_ID = "rideId"

/** The `mageride://…` link, same dictionary. Resolved by the app's `PushRouter`, never trusted. */
private const val KEY_DEEPLINK = "deeplink"

/** An APNs data block is `Map<String, String>` on the wire, and §1.6 stores it verbatim. */
private val PUSH_DATA = MapSerializer(String.serializer(), String.serializer())
