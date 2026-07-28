package lk.mageride.shared.db.passenger

import app.cash.sqldelight.adapter.primitive.IntColumnAdapter
import app.cash.sqldelight.db.QueryResult
import app.cash.sqldelight.db.SqlDriver
import app.cash.sqldelight.db.SqlSchema
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.db.BaseMageRideDb
import lk.mageride.shared.db.BufferedFix
import lk.mageride.shared.db.CommandOutbox
import lk.mageride.shared.db.EpochMillisAdapter
import lk.mageride.shared.db.GpsBufferStore
import lk.mageride.shared.db.GpsSampleState
import lk.mageride.shared.db.MageRideApp
import lk.mageride.shared.db.MapBundleRelease
import lk.mageride.shared.db.MetaStore
import lk.mageride.shared.db.OutboxCommand
import lk.mageride.shared.db.OutboxMethod
import lk.mageride.shared.db.OutboxState
import lk.mageride.shared.db.OutboxStore
import lk.mageride.shared.db.Retention
import lk.mageride.shared.db.RetentionPolicy
import lk.mageride.shared.db.RetentionReport
import lk.mageride.shared.db.RetentionTable
import lk.mageride.shared.db.deleteEveryRow
import lk.mageride.shared.db.prune
import kotlin.time.Instant

/**
 * `mageride_passenger.db` — `mobile_db_schema.md` §1 shared tables + §2 passenger tables.
 *
 * The §1 machinery is reached through [outbox], [meta] and
 * [lk.mageride.shared.db.MageRideDb.gpsBuffer]; the §2 projections through [sql], which is the
 * generated SQLDelight surface (`sql.ridesQueries`, `sql.savedAddressesQueries`, …). There is no
 * hand-written wrapper around those — the passenger app is their only consumer.
 *
 * The three store implementations below are this file's whole reason for existing. SQLDelight
 * generates a **separate** `CommandOutboxQueries` type per database even though `CommandOutbox.sq`
 * is authored once (see `build.gradle.kts`), so the driver database's twin of this file is
 * line-for-line the same shape over a different set of generated types. Everything with a decision
 * in it lives one package up, in common code, and is written once.
 *
 * Blocking. See [lk.mageride.shared.db.MageRideDb].
 */
public class PassengerDb(internal val sqlDriver: SqlDriver) : BaseMageRideDb(MageRideApp.PASSENGER) {

    /** The generated database. §2 projections are read and written through its query classes. */
    public val sql: MageRidePassengerDatabase = MageRidePassengerDatabase(
        driver = sqlDriver,
        auth_sessionAdapter = Auth_session.Adapter(
            access_token_expires_atAdapter = EpochMillisAdapter,
            mqtt_token_expires_atAdapter = EpochMillisAdapter,
            last_refresh_atAdapter = EpochMillisAdapter,
            created_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
        blocked_driversAdapter = Blocked_drivers.Adapter(
            created_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        command_outboxAdapter = Command_outbox.Adapter(
            attemptsAdapter = IntColumnAdapter,
            response_statusAdapter = IntColumnAdapter,
            created_atAdapter = EpochMillisAdapter,
            last_attempt_atAdapter = EpochMillisAdapter,
            next_retry_atAdapter = EpochMillisAdapter,
        ),
        content_templatesAdapter = Content_templates.Adapter(
            versionAdapter = IntColumnAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        emergency_contactsAdapter = Emergency_contacts.Adapter(
            synced_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
        faq_articlesAdapter = Faq_articles.Adapter(
            sort_orderAdapter = IntColumnAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        fare_estimatesAdapter = Fare_estimates.Adapter(
            surcharge_pctAdapter = IntColumnAdapter,
            distance_mAdapter = IntColumnAdapter,
            computed_atAdapter = EpochMillisAdapter,
            expires_atAdapter = EpochMillisAdapter,
        ),
        gps_bufferAdapter = Gps_buffer.Adapter(
            heading_degAdapter = IntColumnAdapter,
            sat_countAdapter = IntColumnAdapter,
            sample_tsAdapter = EpochMillisAdapter,
            sourceAdapter = IntColumnAdapter,
            created_atAdapter = EpochMillisAdapter,
        ),
        location_requestsAdapter = Location_requests.Adapter(
            issued_atAdapter = EpochMillisAdapter,
            ttl_secondsAdapter = IntColumnAdapter,
            resolved_atAdapter = EpochMillisAdapter,
        ),
        metaAdapter = Meta.Adapter(updated_atAdapter = EpochMillisAdapter),
        notificationsAdapter = Notifications.Adapter(received_atAdapter = EpochMillisAdapter),
        offline_map_bundlesAdapter = Offline_map_bundles.Adapter(
            downloaded_atAdapter = EpochMillisAdapter,
            expires_atAdapter = EpochMillisAdapter,
        ),
        place_recentsAdapter = Place_recents.Adapter(
            use_countAdapter = IntColumnAdapter,
            last_used_atAdapter = EpochMillisAdapter,
        ),
        ratings_pendingAdapter = Ratings_pending.Adapter(created_atAdapter = EpochMillisAdapter),
        ridesAdapter = Rides.Adapter(
            kindAdapter = IntColumnAdapter,
            vehicle_heading_degAdapter = IntColumnAdapter,
            offer_expires_atAdapter = EpochMillisAdapter,
            qr_claimed_atAdapter = EpochMillisAdapter,
            created_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
            terminal_atAdapter = EpochMillisAdapter,
            server_updated_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        saved_addressesAdapter = Saved_addresses.Adapter(
            synced_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
        support_ticketsAdapter = Support_tickets.Adapter(
            created_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        trip_sharesAdapter = Trip_shares.Adapter(
            expires_atAdapter = EpochMillisAdapter,
            created_atAdapter = EpochMillisAdapter,
        ),
        user_profileAdapter = User_profile.Adapter(
            synced_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
    )

    override val meta: MetaStore = PassengerMetaStore(sql)

    override val gpsStore: GpsBufferStore = PassengerGpsBufferStore(sql)

    override val outbox: CommandOutbox = CommandOutbox(PassengerOutboxStore(sql))

    override val retention: Retention by lazy { PassengerRetention() }

    override fun <T> transaction(body: () -> T): T = sql.transactionWithResult { body() }

    override fun wipe() {
        forgetGpsBuffers()
        sqlDriver.deleteEveryRow()
    }

    override fun close() {
        forgetGpsBuffers()
        sqlDriver.close()
    }

    /**
     * §4.3 for the passenger tables.
     *
     * `location_requests` and the singleton/profile caches are deliberately untouched — see
     * [RetentionPolicy]. `offline_map_bundles` rows survive too: the sweep only reports the
     * bundles to release, because deleting the row before the app deletes the PMTiles file would
     * orphan the file with nothing pointing at it.
     */
    private inner class PassengerRetention : Retention {
        override fun sweep(now: Instant, policy: RetentionPolicy): RetentionReport {
            val removed = mutableMapOf<RetentionTable, Long>()
            val bundles = mutableListOf<MapBundleRelease>()

            sql.transaction {
                removed.prune(sqlDriver, RetentionTable.COMMAND_OUTBOX) {
                    sql.commandOutboxQueries.deleteAckedBefore(now - policy.outboxAcked)
                }
                removed.prune(sqlDriver, RetentionTable.NOTIFICATIONS) {
                    sql.notificationsQueries.deleteBefore(now - policy.notifications)
                    sql.notificationsQueries.deleteBeyondCap(policy.notificationsMax)
                }
                removed.prune(sqlDriver, RetentionTable.RIDES) {
                    sql.ridesQueries.deleteTerminalBefore(now - policy.rides)
                    sql.ridesQueries.deleteTerminalBeyondCap(policy.ridesMax)
                }
                removed.prune(sqlDriver, RetentionTable.FARE_ESTIMATES) {
                    sql.fareEstimatesQueries.deleteExpired(now)
                }
                removed.prune(sqlDriver, RetentionTable.TRIP_SHARES) {
                    sql.tripSharesQueries.deleteExpired(now - policy.tripShareGrace)
                }
                removed.prune(sqlDriver, RetentionTable.PLACE_RECENTS) {
                    sql.placeRecentsQueries.deleteBeyondCap(policy.placeRecentsMax)
                }
                sql.offlineMapBundlesQueries.selectEvictable(now).executeAsList().forEach {
                    bundles += MapBundleRelease(id = it.id, localPath = it.local_path, sizeBytes = it.size_bytes)
                }
            }

            // Outside the transaction above: the ring is bounded per vehicle and each vehicle's
            // eviction opens its own.
            removed[RetentionTable.GPS_BUFFER] = sweepGpsBuffers(now, policy.gps)

            return RetentionReport(sweptAt = now, removed = removed, mapBundlesToRelease = bundles)
        }
    }

    public companion object {
        /** The schema `MageRideDatabaseFactory` hands the platform driver. */
        public val SCHEMA: SqlSchema<QueryResult.Value<Unit>> = MageRidePassengerDatabase.Schema
    }
}

// ------------------------------------------------------------------------------------------
// Store implementations. Mechanical: map a generated row to the common domain type and back.
// The driver database carries the identical set over its own generated types.
// ------------------------------------------------------------------------------------------

private class PassengerMetaStore(private val sql: MageRidePassengerDatabase) : MetaStore {
    override fun get(key: String): String? = sql.metaQueries.selectRow(key).executeAsOneOrNull()?.value_

    override fun put(key: String, value: String?, now: Instant) {
        sql.metaQueries.put(key, value, now)
    }

    override fun remove(key: String) {
        sql.metaQueries.delete(key)
    }

    override fun all(): Map<String, String?> =
        sql.metaQueries.selectAll().executeAsList().associate { it.key to it.value_ }

    override fun clear() {
        sql.metaQueries.deleteAll()
    }
}

private class PassengerOutboxStore(private val sql: MageRidePassengerDatabase) : OutboxStore {
    private val q get() = sql.commandOutboxQueries

    override fun insert(command: OutboxCommand) {
        q.enqueue(
            idempotency_key = command.idempotencyKey,
            endpoint = command.endpoint,
            http_method = command.method.name,
            command = command.command,
            entity_type = command.entityType,
            entity_id = command.entityId,
            request_body = command.requestBody,
            request_headers = command.requestHeaders,
            created_at = command.createdAt,
            next_retry_at = command.nextRetryAt,
        )
    }

    override fun byKey(key: String): OutboxCommand? = q.selectByKey(key).executeAsOneOrNull()?.toDomain()

    override fun dispatchable(now: Instant, limit: Long): List<OutboxCommand> =
        q.selectDispatchable(now, limit).executeAsList().map { it.toDomain() }

    override fun byState(state: OutboxState): List<OutboxCommand> =
        q.selectByState(state.name).executeAsList().map { it.toDomain() }

    override fun pendingFor(entityType: String, entityId: String): List<OutboxCommand> =
        q.selectPendingForEntity(entityType, entityId).executeAsList().map { it.toDomain() }

    override fun markInflight(key: String, now: Instant) {
        q.claim(now, key)
    }

    override fun markTerminal(key: String, state: OutboxState, status: Int?, body: String?, at: Instant) {
        when (state) {
            OutboxState.ACKED -> q.markAcked(status, body, at, key)
            OutboxState.FAILED -> q.markFailed(status, body, at, key)
            OutboxState.ABANDONED -> q.markAbandoned(status, body, at, key)
            else -> error("$state is not a terminal outbox state")
        }
    }

    override fun markRetrying(key: String, status: Int?, nextRetryAt: Instant, at: Instant) {
        q.reschedule(status, nextRetryAt, at, key)
    }

    override fun resetInflight(nextRetryAt: Instant) {
        q.recoverInflight(nextRetryAt)
    }

    override fun deleteAckedBefore(cutoff: Instant) {
        q.deleteAckedBefore(cutoff)
    }

    override fun delete(key: String) {
        q.dismiss(key)
    }

    override fun deleteAll() {
        q.deleteAll()
    }

    override fun <T> transaction(body: () -> T): T = sql.transactionWithResult { body() }
}

private class PassengerGpsBufferStore(private val sql: MageRidePassengerDatabase) : GpsBufferStore {
    private val q get() = sql.gpsBufferQueries

    override fun insert(fix: BufferedFix) {
        q.append(
            seq = fix.seq,
            vehicle_id = fix.vehicleId,
            lat = fix.lat,
            lng = fix.lng,
            accuracy_m = fix.accuracyM,
            speed_mps = fix.speedMps,
            heading_deg = fix.headingDeg,
            hdop = fix.hdop,
            sat_count = fix.satCount,
            sample_ts = fix.sampleTs,
            source = fix.source.code,
            state = fix.state.name,
            created_at = fix.createdAt,
        )
    }

    override fun replayBatch(vehicleId: Ulid, limit: Long): List<BufferedFix> =
        q.selectReplayBatch(vehicleId, limit).executeAsList().map { it.toDomain() }

    override fun all(vehicleId: Ulid): List<BufferedFix> =
        q.selectByVehicle(vehicleId).executeAsList().map { it.toDomain() }

    override fun vehicles(): List<Ulid> = q.distinctVehicles().executeAsList()

    override fun highestSeq(vehicleId: Ulid): Long? = q.highestSeq(vehicleId).executeAsOne().highest

    override fun count(vehicleId: Ulid): Long = q.countBuffered(vehicleId).executeAsOne()

    override fun setState(vehicleId: Ulid, seq: Long, state: GpsSampleState) {
        when (state) {
            GpsSampleState.PUBLISHED -> q.markPublished(vehicleId, seq)
            GpsSampleState.REPLAY_PENDING -> q.markReplayPending(vehicleId, seq)
            GpsSampleState.ACKED -> q.markAckedThrough(vehicleId, seq)
            GpsSampleState.PENDING -> q.markPending(vehicleId, seq)
        }
    }

    override fun ackThrough(vehicleId: Ulid, seq: Long) {
        q.markAckedThrough(vehicleId, seq)
    }

    override fun resetInFlight(vehicleId: Ulid) {
        q.resetInFlight(vehicleId)
    }

    override fun deleteDelivered(vehicleId: Ulid) {
        q.deleteAcked(vehicleId)
    }

    override fun deleteOlderThan(vehicleId: Ulid, cutoff: Instant) {
        q.deleteOlderThan(vehicleId, cutoff)
    }

    override fun deleteOldest(vehicleId: Ulid, count: Long) {
        q.deleteOldestBeyond(vehicleId, count)
    }

    override fun deleteVehicle(vehicleId: Ulid) {
        q.deleteVehicle(vehicleId)
    }

    override fun deleteAll() {
        q.deleteAll()
    }

    override fun <T> transaction(body: () -> T): T = sql.transactionWithResult { body() }
}

// A CHECK constraint guards every one of these columns, so an unmapped value means the file has
// been altered outside the app. Failing loudly is the safe answer: silently reading an unknown
// `state` as PENDING would re-send a command the server already applied, and an unknown
// `http_method` would re-send a DELETE as a POST.
private fun Command_outbox.toDomain(): OutboxCommand = OutboxCommand(
    idempotencyKey = idempotency_key,
    endpoint = endpoint,
    method = OutboxMethod.fromWire(http_method) ?: error("command_outbox.http_method='$http_method'"),
    command = command,
    requestBody = request_body,
    createdAt = created_at,
    entityType = entity_type,
    entityId = entity_id,
    requestHeaders = request_headers,
    state = OutboxState.fromWire(state) ?: error("command_outbox.state='$state'"),
    attempts = attempts,
    responseStatus = response_status,
    responseBody = response_body,
    lastAttemptAt = last_attempt_at,
    nextRetryAt = next_retry_at,
)

private fun Gps_buffer.toDomain(): BufferedFix = BufferedFix(
    vehicleId = vehicle_id,
    seq = seq,
    lat = lat,
    lng = lng,
    sampleTs = sample_ts,
    createdAt = created_at,
    accuracyM = accuracy_m,
    speedMps = speed_mps,
    headingDeg = heading_deg,
    hdop = hdop,
    satCount = sat_count,
    source = PositionSource.fromCode(source) ?: error("gps_buffer.source=$source"),
    state = GpsSampleState.fromWire(state) ?: error("gps_buffer.state='$state'"),
)
