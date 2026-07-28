package lk.mageride.shared.db

import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.platform.SecureStore
import kotlin.time.Instant

// In-memory stand-ins for the three §1 machinery tables.
//
// They exist so the RULES can be tested on every target — commonTest compiles for iOS too, and
// there is no SQLite driver there on this build host. The same rules are then exercised against a
// real engine in androidHostTest (`CommandOutboxDurabilityTest`, `GpsBufferTest`), which is what
// proves the SQL underneath agrees with them.

internal val T0: Instant = Instant.parse("2026-07-27T00:00:00Z")

internal class FakeMetaStore : MetaStore {
    val values: MutableMap<String, String?> = mutableMapOf()
    var writes: Int = 0
        private set

    override fun get(key: String): String? = values[key]

    override fun put(key: String, value: String?, now: Instant) {
        values[key] = value
        writes++
    }

    override fun remove(key: String) {
        values.remove(key)
    }

    override fun all(): Map<String, String?> = values.toMap()

    override fun clear() {
        values.clear()
    }
}

internal class FakeOutboxStore : OutboxStore {
    val rows: MutableMap<String, OutboxCommand> = linkedMapOf()

    override fun insert(command: OutboxCommand) {
        // Mirrors the plain INSERT the real query runs: the primary key raises on a duplicate, so
        // a CommandOutbox that forgot to check first would fail this test rather than pass quietly.
        require(!rows.containsKey(command.idempotencyKey)) { "duplicate key ${command.idempotencyKey}" }
        rows[command.idempotencyKey] = command
    }

    override fun byKey(key: String): OutboxCommand? = rows[key]

    override fun dispatchable(now: Instant, limit: Long): List<OutboxCommand> = rows.values
        .filter { it.state == OutboxState.PENDING && (it.nextRetryAt == null || it.nextRetryAt <= now) }
        .sortedWith(compareBy({ it.createdAt }, { it.idempotencyKey }))
        .take(limit.toInt())

    override fun byState(state: OutboxState): List<OutboxCommand> =
        rows.values.filter { it.state == state }.sortedBy { it.createdAt }

    override fun pendingFor(entityType: String, entityId: String): List<OutboxCommand> = rows.values
        .filter {
            it.entityType == entityType && it.entityId == entityId &&
                (it.state == OutboxState.PENDING || it.state == OutboxState.INFLIGHT)
        }
        .sortedBy { it.createdAt }

    override fun markInflight(key: String, now: Instant) {
        rows[key]?.let {
            rows[key] = it.copy(state = OutboxState.INFLIGHT, attempts = it.attempts + 1, lastAttemptAt = now)
        }
    }

    override fun markTerminal(key: String, state: OutboxState, status: Int?, body: String?, at: Instant) {
        rows[key]?.let {
            rows[key] = it.copy(
                state = state,
                responseStatus = status,
                responseBody = body,
                nextRetryAt = null,
                lastAttemptAt = at,
            )
        }
    }

    override fun markRetrying(key: String, status: Int?, nextRetryAt: Instant, at: Instant) {
        rows[key]?.let {
            rows[key] = it.copy(
                state = OutboxState.PENDING,
                responseStatus = status,
                nextRetryAt = nextRetryAt,
                lastAttemptAt = at,
            )
        }
    }

    override fun resetInflight(nextRetryAt: Instant) {
        rows.keys.toList().forEach { key ->
            val row = rows.getValue(key)
            if (row.state == OutboxState.INFLIGHT) {
                rows[key] = row.copy(state = OutboxState.PENDING, nextRetryAt = nextRetryAt)
            }
        }
    }

    override fun deleteAckedBefore(cutoff: Instant) {
        rows.entries.removeAll { (_, row) ->
            row.state == OutboxState.ACKED && (row.lastAttemptAt ?: row.createdAt) < cutoff
        }
    }

    override fun delete(key: String) {
        rows.remove(key)
    }

    override fun deleteAll() {
        rows.clear()
    }

    override fun <T> transaction(body: () -> T): T = body()
}

internal class FakeGpsBufferStore : GpsBufferStore {
    val rows: MutableList<BufferedFix> = mutableListOf()

    override fun insert(fix: BufferedFix) {
        if (rows.none { it.vehicleId == fix.vehicleId && it.seq == fix.seq }) rows += fix
    }

    override fun replayBatch(vehicleId: Ulid, limit: Long): List<BufferedFix> = rows
        .filter {
            it.vehicleId == vehicleId &&
                (it.state == GpsSampleState.PENDING || it.state == GpsSampleState.REPLAY_PENDING)
        }
        .sortedBy { it.seq }
        .take(limit.toInt())

    override fun all(vehicleId: Ulid): List<BufferedFix> = rows.filter { it.vehicleId == vehicleId }.sortedBy { it.seq }

    override fun vehicles(): List<Ulid> = rows.map { it.vehicleId }.distinct().sorted()

    override fun highestSeq(vehicleId: Ulid): Long? = rows.filter { it.vehicleId == vehicleId }.maxOfOrNull { it.seq }

    override fun count(vehicleId: Ulid): Long = rows.count { it.vehicleId == vehicleId }.toLong()

    override fun setState(vehicleId: Ulid, seq: Long, state: GpsSampleState) {
        replace(vehicleId, { it.seq == seq }, state)
    }

    override fun ackThrough(vehicleId: Ulid, seq: Long) {
        replace(vehicleId, { it.seq <= seq }, GpsSampleState.ACKED)
    }

    override fun resetInFlight(vehicleId: Ulid) {
        replace(vehicleId, { it.state == GpsSampleState.REPLAY_PENDING }, GpsSampleState.PENDING)
    }

    override fun deleteDelivered(vehicleId: Ulid) {
        rows.removeAll { it.vehicleId == vehicleId && it.state.isDelivered }
    }

    override fun deleteOlderThan(vehicleId: Ulid, cutoff: Instant) {
        rows.removeAll { it.vehicleId == vehicleId && it.createdAt < cutoff }
    }

    override fun deleteOldest(vehicleId: Ulid, count: Long) {
        val doomed = rows.filter { it.vehicleId == vehicleId }.sortedBy { it.seq }.take(count.toInt()).toSet()
        rows.removeAll(doomed)
    }

    override fun deleteVehicle(vehicleId: Ulid) {
        rows.removeAll { it.vehicleId == vehicleId }
    }

    override fun deleteAll() {
        rows.clear()
    }

    override fun <T> transaction(body: () -> T): T = body()

    private fun replace(vehicleId: Ulid, match: (BufferedFix) -> Boolean, state: GpsSampleState) {
        rows.indices.forEach { i ->
            val row = rows[i]
            if (row.vehicleId == vehicleId && match(row)) rows[i] = row.copy(state = state)
        }
    }
}

internal class RecordingSecureStore : SecureStore {
    val values: MutableMap<String, String> = mutableMapOf()

    override suspend fun read(key: String): String? = values[key]

    override suspend fun write(key: String, value: String) {
        values[key] = value
    }

    override suspend fun delete(key: String) {
        values.remove(key)
    }

    override suspend fun clear() {
        values.clear()
    }
}

internal fun command(
    key: String,
    createdAt: Instant = T0,
    entityType: String? = null,
    entityId: String? = null,
    method: OutboxMethod = OutboxMethod.POST,
): OutboxCommand = OutboxCommand(
    idempotencyKey = key,
    endpoint = "/v1/rides/request",
    method = method,
    command = "ride.request",
    requestBody = """{"clientRequestId":"$key"}""",
    createdAt = createdAt,
    entityType = entityType,
    entityId = entityId,
)
