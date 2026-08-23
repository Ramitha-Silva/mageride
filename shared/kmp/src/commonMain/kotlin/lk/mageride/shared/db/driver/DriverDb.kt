package lk.mageride.shared.db.driver

import app.cash.sqldelight.adapter.primitive.IntColumnAdapter
import app.cash.sqldelight.db.QueryResult
import app.cash.sqldelight.db.SqlDriver
import app.cash.sqldelight.db.SqlSchema
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.db.BaseMageRideDb
import lk.mageride.shared.db.BufferedFix
import lk.mageride.shared.db.BusinessDateAdapter
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
 * `mageride_driver.db` — `mobile_db_schema.md` §1 shared tables + §3 driver tables.
 *
 * The twin of [lk.mageride.shared.db.passenger.PassengerDb], over the driver database's own
 * generated types. See that file's KDoc for why the two exist rather than one: SQLDelight emits a
 * separate `CommandOutboxQueries` per database even though the §1 tables are authored once.
 *
 * This is the database the GPS ring actually gets used on (§1.5 — "primarily produced by the
 * Driver app"), and the one that carries `proof_upload_queue`, the second durable queue (P-10).
 *
 * Blocking. See [lk.mageride.shared.db.MageRideDb].
 */
public class DriverDb(internal val sqlDriver: SqlDriver) : BaseMageRideDb(MageRideApp.DRIVER) {

    /** The generated database. §3 projections are read and written through its query classes. */
    public val sql: MageRideDriverDatabase = MageRideDriverDatabase(
        driver = sqlDriver,
        active_rideAdapter = Active_ride.Adapter(
            kindAdapter = IntColumnAdapter,
            qr_claimed_atAdapter = EpochMillisAdapter,
            created_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
            server_updated_atAdapter = EpochMillisAdapter,
        ),
        auth_sessionAdapter = Auth_session.Adapter(
            access_token_expires_atAdapter = EpochMillisAdapter,
            mqtt_token_expires_atAdapter = EpochMillisAdapter,
            last_refresh_atAdapter = EpochMillisAdapter,
            created_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
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
        credit_transfersAdapter = Credit_transfers.Adapter(
            created_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        daily_fee_statusAdapter = Daily_fee_status.Adapter(
            fee_dateAdapter = BusinessDateAdapter,
            trips_that_dayAdapter = IntColumnAdapter,
            updated_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        directional_filterAdapter = Directional_filter.Adapter(
            set_atAdapter = EpochMillisAdapter,
            expires_atAdapter = EpochMillisAdapter,
            uses_todayAdapter = IntColumnAdapter,
            max_uses_per_dayAdapter = IntColumnAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
        dispatch_offersAdapter = Dispatch_offers.Adapter(
            distance_to_pickup_mAdapter = IntColumnAdapter,
            kindAdapter = IntColumnAdapter,
            sent_atAdapter = EpochMillisAdapter,
            expires_atAdapter = EpochMillisAdapter,
        ),
        documentsAdapter = Documents.Adapter(
            expires_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        driver_earningsAdapter = Driver_earnings.Adapter(
            earn_dateAdapter = BusinessDateAdapter,
            tripsAdapter = IntColumnAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        driver_levelAdapter = Driver_level.Adapter(
            levelAdapter = IntColumnAdapter,
            rating_pointsAdapter = IntColumnAdapter,
            level_up_thresholdAdapter = IntColumnAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        // Δ MCS-27 — §3.16's first-frame cache.
        driver_profileAdapter = Driver_profile.Adapter(
            levelAdapter = IntColumnAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        // Δ MCS-28 — §3.17's document images. Both timestamps, no adapted Int: `side` and `kind`
        // stay TEXT for the reason every other enum-ish column here does — an adapter that threw on
        // an unknown value would crash the app on a deploy that added a document kind.
        document_imagesAdapter = Document_images.Adapter(
            cached_atAdapter = EpochMillisAdapter,
            expires_atAdapter = EpochMillisAdapter,
        ),
        emergency_contactsAdapter = Emergency_contacts.Adapter(
            synced_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
        faq_articlesAdapter = Faq_articles.Adapter(
            sort_orderAdapter = IntColumnAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        gps_bufferAdapter = Gps_buffer.Adapter(
            heading_degAdapter = IntColumnAdapter,
            sat_countAdapter = IntColumnAdapter,
            sample_tsAdapter = EpochMillisAdapter,
            sourceAdapter = IntColumnAdapter,
            created_atAdapter = EpochMillisAdapter,
        ),
        job_boardAdapter = Job_board.Adapter(
            pickup_timeAdapter = EpochMillisAdapter,
            distance_mAdapter = IntColumnAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        metaAdapter = Meta.Adapter(updated_atAdapter = EpochMillisAdapter),
        notificationsAdapter = Notifications.Adapter(received_atAdapter = EpochMillisAdapter),
        offline_map_bundlesAdapter = Offline_map_bundles.Adapter(
            downloaded_atAdapter = EpochMillisAdapter,
            expires_atAdapter = EpochMillisAdapter,
        ),
        proof_upload_queueAdapter = Proof_upload_queue.Adapter(
            captured_atAdapter = EpochMillisAdapter,
            attemptsAdapter = IntColumnAdapter,
            next_retry_atAdapter = EpochMillisAdapter,
        ),
        ratings_pendingAdapter = Ratings_pending.Adapter(created_atAdapter = EpochMillisAdapter),
        ride_historyAdapter = Ride_history.Adapter(
            kindAdapter = IntColumnAdapter,
            completed_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        standby_stateAdapter = Standby_state.Adapter(
            pos_rate_interval_msAdapter = IntColumnAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
        support_ticketsAdapter = Support_tickets.Adapter(
            created_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        user_profileAdapter = User_profile.Adapter(
            synced_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
        vehiclesAdapter = Vehicles.Adapter(
            synced_atAdapter = EpochMillisAdapter,
            updated_atAdapter = EpochMillisAdapter,
        ),
        walletAdapter = Wallet.Adapter(
            updated_atAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
        wallet_transactionsAdapter = Wallet_transactions.Adapter(
            tsAdapter = EpochMillisAdapter,
            synced_atAdapter = EpochMillisAdapter,
        ),
    )

    override val meta: MetaStore = DriverMetaStore(sql)

    override val gpsStore: GpsBufferStore = DriverGpsBufferStore(sql)

    override val outbox: CommandOutbox = CommandOutbox(DriverOutboxStore(sql))

    override val retention: Retention by lazy { DriverRetention() }

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
     * §4.3 for the driver tables.
     *
     * `proof_upload_queue` only ever loses `UPLOADED` rows — a `FAILED` proof is kept for manual
     * retry (§4.3) and its file must not be deleted either, because P-10 makes the photo evidence
     * of a delivery. The earnings, daily-fee, wallet, vehicle, document and level caches are not
     * swept: they are bounded by the account and a driver offline for a week still needs the
     * dashboard to render.
     */
    private inner class DriverRetention : Retention {
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
                removed.prune(sqlDriver, RetentionTable.RIDE_HISTORY) {
                    sql.rideHistoryQueries.deleteBefore(now - policy.rides)
                    sql.rideHistoryQueries.deleteBeyondCap(policy.ridesMax)
                }
                removed.prune(sqlDriver, RetentionTable.DISPATCH_OFFERS) {
                    sql.dispatchOffersQueries.deleteExpiredBefore(now - policy.offerGrace)
                }
                removed.prune(sqlDriver, RetentionTable.PROOF_UPLOAD_QUEUE) {
                    sql.proofUploadQueueQueries.deleteUploaded()
                }
                removed.prune(sqlDriver, RetentionTable.JOB_BOARD) {
                    sql.jobBoardQueries.deletePast(now - policy.jobBoardGrace)
                }
                sql.offlineMapBundlesQueries.selectEvictable(now).executeAsList().forEach {
                    bundles += MapBundleRelease(id = it.id, localPath = it.local_path, sizeBytes = it.size_bytes)
                }
            }

            removed[RetentionTable.GPS_BUFFER] = sweepGpsBuffers(now, policy.gps)

            return RetentionReport(sweptAt = now, removed = removed, mapBundlesToRelease = bundles)
        }
    }

    public companion object {
        /** The schema `MageRideDatabaseFactory` hands the platform driver. */
        public val SCHEMA: SqlSchema<QueryResult.Value<Unit>> = MageRideDriverDatabase.Schema
    }
}

// ------------------------------------------------------------------------------------------
// Store implementations — the twins of PassengerDb's, over the driver database's generated types.
// ------------------------------------------------------------------------------------------

private class DriverMetaStore(private val sql: MageRideDriverDatabase) : MetaStore {
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

private class DriverOutboxStore(private val sql: MageRideDriverDatabase) : OutboxStore {
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

private class DriverGpsBufferStore(private val sql: MageRideDriverDatabase) : GpsBufferStore {
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
            GpsSampleState.PENDING -> q.markPending(vehicleId, seq)
            GpsSampleState.PUBLISHED -> q.markPublished(vehicleId, seq)
            GpsSampleState.REPLAY_PENDING -> q.markReplayPending(vehicleId, seq)
            GpsSampleState.ACKED -> q.markAckedThrough(vehicleId, seq)
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

// See PassengerDb for why an unmapped value is an error rather than a fallback.
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
