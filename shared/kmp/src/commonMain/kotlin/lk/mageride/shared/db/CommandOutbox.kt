package lk.mageride.shared.db

import kotlin.math.pow
import kotlin.random.Random
import kotlin.time.Duration
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.seconds
import kotlin.time.Instant

// The durable idempotent command queue — mobile_db_schema.md §1.4 + §4.1, R-14, R-18, ADD §11.13.
//
// The rule the whole design serves: NO USER ACTION IS EVER LOST, AND NO USER ACTION IS EVER
// APPLIED TWICE. Locally that means the row is written in the same transaction as the optimistic
// projection and outlives any crash; on the wire it means the same `Idempotency-Key` on every
// attempt, so a retry after an unknown outcome is a REPLAY — the server returns the response it
// stored in `rides.command_log` (C004) instead of executing again.
//
// The key is minted here, not by the HTTP layer: C013's `IdempotencyKey` is minted before the
// first send and never re-minted, and an outbox row IS that first send. Pass
// `OutboxCommand.idempotencyKey` into the client call so the two agree.

/** The four verbs `command_outbox.http_method` admits. */
public enum class OutboxMethod {
    POST,
    PUT,
    PATCH,
    DELETE,
    ;

    public companion object {
        /** The stored spelling, or `null` when it is not one of the four. */
        public fun fromWire(wire: String): OutboxMethod? = entries.firstOrNull { it.name == wire }
    }
}

/**
 * Local delivery state — **not** the server's view of the command.
 *
 * `ACKED` means a 2xx came back (possibly a replayed one). `FAILED` means a non-retryable
 * response the UI has to surface. `ABANDONED` means the retry budget ran out with the outcome
 * still unknown — the one state where the app genuinely does not know whether the server applied
 * the command, and the reason it is distinct from `FAILED`.
 */
public enum class OutboxState {
    PENDING,
    INFLIGHT,
    ACKED,
    FAILED,
    ABANDONED,
    ;

    /** Whether the drain loop will ever look at a row in this state again. */
    public val isTerminal: Boolean get() = this == ACKED || this == FAILED || this == ABANDONED

    public companion object {
        /** The stored spelling, or `null` when the CHECK domain has moved under us. */
        public fun fromWire(wire: String): OutboxState? = entries.firstOrNull { it.name == wire }
    }
}

/**
 * One queued mutating call.
 *
 * @property idempotencyKey Client ULID. Primary key here and the `Idempotency-Key` header there.
 * @property endpoint The path, as it will be called.
 * @property method HTTP verb.
 * @property command Logical name — `'ride.accept'`, `'fare.qr_claim'`. What the UI shows when the
 *   command fails; never the endpoint.
 * @property entityType Which projection this edit belongs to (`'ride'`, `'address'`, …). Together
 *   with [entityId] it is how §4.2's conflict rule finds the pending edit for a `dirty` row.
 * @property entityId The projection's primary key.
 * @property requestBody JSON payload, already serialised.
 * @property requestHeaders JSON of NON-SECRET headers only. No bearer token, ever — §0.4 keeps
 *   credentials in the Keystore and C013 attaches them at send time.
 * @property attempts How many times [CommandOutbox.claim] has handed this row to a sender.
 * @property nextRetryAt When the drain query will pick it up again.
 */
public data class OutboxCommand(
    val idempotencyKey: String,
    val endpoint: String,
    val method: OutboxMethod,
    val command: String,
    val requestBody: String,
    val createdAt: Instant,
    val entityType: String? = null,
    val entityId: String? = null,
    val requestHeaders: String? = null,
    val state: OutboxState = OutboxState.PENDING,
    val attempts: Int = 0,
    val responseStatus: Int? = null,
    val responseBody: String? = null,
    val lastAttemptAt: Instant? = null,
    val nextRetryAt: Instant? = null,
)

/** What [CommandOutbox] did with a response. */
public sealed interface OutboxOutcome {

    /** 2xx (or a replayed 2xx). The projection may clear its `dirty` flag. */
    public data class Acked(val command: OutboxCommand) : OutboxOutcome

    /** Transient — queued again for [at]. */
    public data class Retrying(val command: OutboxCommand, val at: Instant) : OutboxOutcome

    /** Non-retryable. The UI has to tell the user; the row stays until dismissed (§4.3). */
    public data class Failed(val command: OutboxCommand) : OutboxOutcome

    /** Retry budget exhausted with the outcome unknown. */
    public data class Abandoned(val command: OutboxCommand) : OutboxOutcome

    /** The key names no row — already pruned, or never enqueued. */
    public data object Unknown : OutboxOutcome
}

/**
 * When and how often the drain loop retries — §4.1 step 3, ADD §7.5.3.
 *
 * The curve is the platform's reconnect curve: **1 s to 60 s, exponential, ±25 % symmetric
 * jitter**, the same numbers [lk.mageride.shared.util.ReconnectBackoff] applies to MQTT and the
 * SignalR hub and for the same reason — a regional outage ends for every handset in a cell at
 * once, and an unjittered queue turns that into a synchronised POST wave. It is deliberately
 * *not* C013's in-request [lk.mageride.shared.data.api.RetryPolicy] (3 attempts, 100 ms → 2 s):
 * that one is a single call's stutter, this one is a queue that may be draining a night's worth
 * of actions.
 *
 * **[maxAge] and [maxAttempts] are C018's, not the spec's.** `mobile_db_schema.md` names the
 * `ABANDONED` state but fixes no budget for reaching it, and §4.3 gives a retention rule for
 * `ACKED` and `FAILED` but not for `ABANDONED`. Age is the primary bound because a queue that is
 * offline for a night is normal and one that is offline for a day is not; the attempt cap is a
 * backstop for a command that fails fast in a loop.
 *
 * @property initial First delay, after the first failed attempt.
 * @property max Ceiling of the base delay, before jitter.
 * @property jitterFraction Half-width of the symmetric band.
 * @property maxAttempts Hard cap on [OutboxCommand.attempts].
 * @property maxAge How long a command may stay undelivered before it is abandoned.
 * @property ackedRetention §4.3 — `ACKED` rows are kept this long so a duplicate tap replays
 *   locally instead of minting a second key.
 */
public data class OutboxRetryPolicy(
    val initial: Duration = 1.seconds,
    val max: Duration = 60.seconds,
    val jitterFraction: Double = 0.25,
    val maxAttempts: Int = 50,
    val maxAge: Duration = 24.hours,
    val ackedRetention: Duration = 24.hours,
) {
    init {
        require(initial > Duration.ZERO) { "initial must be positive" }
        require(max >= initial) { "max must be at least initial" }
        require(jitterFraction in 0.0..1.0) { "jitterFraction must be between 0 and 1" }
        require(maxAttempts > 0) { "maxAttempts must be positive" }
    }

    /**
     * The wait before the next attempt.
     *
     * @param attempts How many attempts have already been made — 1 after the first failure.
     */
    public fun backoffFor(attempts: Int, random: Random = Random.Default): Duration {
        val steps = (attempts - 1).coerceAtLeast(0).coerceAtMost(MAX_STEPS)
        val base = (initial * 2.0.pow(steps)).coerceAtMost(max)
        // `nextDouble(-0.0, 0.0)` throws on an empty range, so a zero-jitter policy — which a test
        // asserting the bare curve wants — has to short-circuit rather than draw.
        if (jitterFraction == 0.0) return base
        val factor = 1.0 + random.nextDouble(-jitterFraction, jitterFraction)
        return base * factor
    }

    /**
     * Whether an HTTP status is worth trying again.
     *
     * `408 Request Timeout`, `425 Too Early`, `429 Too Many Requests` and every 5xx are transient.
     * Everything else in the 4xx range is the request's own fault and will fail identically
     * forever — including `409`, which under C002's kernel means the idempotency key was reused
     * with a different body, i.e. a bug that no amount of retrying fixes.
     */
    public fun isRetryable(status: Int): Boolean =
        status >= HTTP_SERVER_ERROR || status == HTTP_TIMEOUT || status == HTTP_TOO_EARLY || status == HTTP_TOO_MANY

    private companion object {
        const val MAX_STEPS = 16
        const val HTTP_TIMEOUT = 408
        const val HTTP_TOO_EARLY = 425
        const val HTTP_TOO_MANY = 429
        const val HTTP_SERVER_ERROR = 500
    }
}

/**
 * Reads and writes `command_outbox`.
 *
 * One implementation per database (`PassengerDb` / `DriverDb`) because SQLDelight generates a
 * separate `CommandOutboxQueries` type per database even though the table is authored once — see
 * `build.gradle.kts`. Everything above this interface is common.
 */
public interface OutboxStore {

    /**
     * Inserts a row the caller has already established is absent.
     *
     * A plain `INSERT`: a duplicate key raises, and so does a violated CHECK. `INSERT OR IGNORE`
     * would swallow both, and an outbox that silently drops a command is worse than one that
     * crashes — see the query's comment in `CommandOutbox.sq`.
     */
    public fun insert(command: OutboxCommand)

    /** The row for [key], or `null`. */
    public fun byKey(key: String): OutboxCommand?

    /** `PENDING` rows whose `next_retry_at` has arrived, oldest first. */
    public fun dispatchable(now: Instant, limit: Long): List<OutboxCommand>

    /** Every row in [state], oldest first. */
    public fun byState(state: OutboxState): List<OutboxCommand>

    /** Unfinished commands for one projection row — §4.2's conflict rule. */
    public fun pendingFor(entityType: String, entityId: String): List<OutboxCommand>

    /** `PENDING` -> `INFLIGHT`, counting the attempt. */
    public fun markInflight(key: String, now: Instant)

    /** Terminal outcome. */
    public fun markTerminal(key: String, state: OutboxState, status: Int?, body: String?, at: Instant)

    /** Back to `PENDING` for another go at [nextRetryAt]. Does not touch `attempts`. */
    public fun markRetrying(key: String, status: Int?, nextRetryAt: Instant, at: Instant)

    /** Cold start: every `INFLIGHT` row back to `PENDING`, due at [nextRetryAt]. */
    public fun resetInflight(nextRetryAt: Instant)

    /** Drops `ACKED` rows last touched before [cutoff] (§4.3). */
    public fun deleteAckedBefore(cutoff: Instant)

    /** Drops one row — the user dismissed a `FAILED` command. */
    public fun delete(key: String)

    /** Drops every row. */
    public fun deleteAll()

    /** Runs [body] in one database transaction. */
    public fun <T> transaction(body: () -> T): T
}

/**
 * The drain loop's state machine over [OutboxStore].
 *
 * A worker's cycle is: [recover] once at start-up, then repeatedly [dispatchable] → [claim] →
 * send → [onResponse] or [onTransportFailure], with [prune] on a slow timer.
 *
 * **Nothing here performs I/O over the network**, deliberately: the sender is C013's typed API
 * client and the worker is the app's (WorkManager on Android, BGTaskScheduler on iOS). This class
 * owns only what must be identical on both — when a command is due, what an attempt costs it, and
 * which responses end it.
 *
 * Calls are **blocking**; SQLDelight's synchronous drivers are. Run the drain off the main thread.
 *
 * @param store The table.
 * @param policy Retry curve and budgets.
 * @param random Injectable so a test can assert the jitter band rather than sample it.
 */
public class CommandOutbox(
    private val store: OutboxStore,
    public val policy: OutboxRetryPolicy = OutboxRetryPolicy(),
    private val random: Random = Random.Default,
) {

    /**
     * Queues a command, or returns the one already queued under the same key.
     *
     * **Call this inside the same transaction as the optimistic local write** (§4.1 step 2) —
     * [OutboxStore.transaction] on the same store, or the enclosing [MageRideDb] transaction.
     * Split across two transactions, a crash between them either loses the user's action or shows
     * them an edit that will never be sent.
     *
     * Re-enqueueing an existing key is a no-op and returns the stored row, so a user tapping
     * "Retry" on a screen that re-issues the same command cannot double-book (R-18).
     */
    public fun enqueue(command: OutboxCommand): OutboxCommand = store.transaction {
        val existing = store.byKey(command.idempotencyKey)
        if (existing != null) {
            existing
        } else {
            store.insert(command)
            store.byKey(command.idempotencyKey) ?: command
        }
    }

    /**
     * Cold start (R-14): re-queues everything the last process died holding.
     *
     * An `INFLIGHT` row is one whose outcome is unknown — the request may have reached the server,
     * been applied, and had its response lost with the process. The safe move is to send it again
     * under the same key: the server replays its stored response rather than executing twice
     * (ADD §11.13). Dropping it would silently lose a user action; executing it blind would double
     * it. **This is the mechanism the "survives a restart, replayed exactly once" guarantee rests
     * on.**
     *
     * @return the commands that were re-queued.
     */
    public fun recover(now: Instant): List<OutboxCommand> = store.transaction {
        val inflight = store.byState(OutboxState.INFLIGHT)
        if (inflight.isNotEmpty()) store.resetInflight(now)
        inflight
    }

    /** Commands due to be sent right now, oldest first. */
    public fun dispatchable(now: Instant, limit: Int = DEFAULT_BATCH): List<OutboxCommand> =
        store.dispatchable(now, limit.toLong())

    /**
     * Takes ownership of one command, counting the attempt.
     *
     * @return the claimed row, or `null` when it is gone or no longer `PENDING` — which is how two
     *   drainers racing the same key resolve without either sending twice.
     */
    public fun claim(key: String, now: Instant): OutboxCommand? = store.transaction {
        val current = store.byKey(key)
        if (current == null || current.state != OutboxState.PENDING) {
            null
        } else {
            store.markInflight(key, now)
            store.byKey(key)
        }
    }

    /**
     * Records what the server said.
     *
     * 2xx acks. A retryable status goes back in the queue unless the budget is spent. Anything
     * else fails and stays for the user to see.
     *
     * @param body The response, verbatim. Stored so a screen can render the server's own answer
     *   after a restart; never parsed here.
     */
    public fun onResponse(key: String, status: Int, body: String?, now: Instant): OutboxOutcome = store.transaction {
        val current = store.byKey(key) ?: return@transaction OutboxOutcome.Unknown
        when {
            status in HTTP_OK until HTTP_REDIRECT -> terminal(current, OutboxState.ACKED, status, body, now)
            policy.isRetryable(status) -> retryOrAbandon(current, status, body, now)
            else -> terminal(current, OutboxState.FAILED, status, body, now)
        }
    }

    /**
     * Records that the request never got an answer — no route, socket closed, TLS failure.
     *
     * Always retryable: the command may or may not have been applied, and the same key makes the
     * next attempt a replay either way. This is the ordinary offline path and it is why the queue
     * exists.
     */
    public fun onTransportFailure(key: String, now: Instant): OutboxOutcome = store.transaction {
        val current = store.byKey(key) ?: return@transaction OutboxOutcome.Unknown
        retryOrAbandon(current, status = null, body = null, now = now)
    }

    /**
     * One queued command, whatever state it is in.
     *
     * The drain loop does not need this — [dispatchable] answers its question — but a screen
     * restarted mid-flow does: an `ACKED` row still holds the server's response verbatim, so the
     * outcome of the last command can be rendered without going back to the network.
     */
    public fun byKey(key: String): OutboxCommand? = store.byKey(key)

    /** Every command in [state], oldest first. Diagnostics and the failed-command list. */
    public fun byState(state: OutboxState): List<OutboxCommand> = store.byState(state)

    /** Unfinished commands for one projection row — §4.2's conflict rule reads this. */
    public fun pendingFor(entityType: String, entityId: String): List<OutboxCommand> =
        store.pendingFor(entityType, entityId)

    /** Everything a screen should be showing as failed (§4.3 keeps these until dismissed). */
    public fun failed(): List<OutboxCommand> = store.byState(OutboxState.FAILED)

    /** The user dismissed a failed command. */
    public fun dismiss(key: String) {
        store.delete(key)
    }

    /** §4.3 retention: drops `ACKED` rows older than [OutboxRetryPolicy.ackedRetention]. */
    public fun prune(now: Instant) {
        store.deleteAckedBefore(now - policy.ackedRetention)
    }

    private fun retryOrAbandon(current: OutboxCommand, status: Int?, body: String?, now: Instant): OutboxOutcome {
        val exhausted = current.attempts >= policy.maxAttempts || now - current.createdAt >= policy.maxAge
        if (exhausted) return terminal(current, OutboxState.ABANDONED, status, body, now)
        val at = now + policy.backoffFor(current.attempts, random)
        store.markRetrying(current.idempotencyKey, status, at, now)
        val updated = store.byKey(current.idempotencyKey) ?: current
        return OutboxOutcome.Retrying(updated, at)
    }

    private fun terminal(
        current: OutboxCommand,
        state: OutboxState,
        status: Int?,
        body: String?,
        now: Instant,
    ): OutboxOutcome {
        store.markTerminal(current.idempotencyKey, state, status, body, now)
        val updated = store.byKey(current.idempotencyKey) ?: current.copy(state = state)
        return when (state) {
            OutboxState.ACKED -> OutboxOutcome.Acked(updated)
            OutboxState.FAILED -> OutboxOutcome.Failed(updated)
            else -> OutboxOutcome.Abandoned(updated)
        }
    }

    public companion object {
        /** How many commands one drain pass takes. Small: each one is a round trip. */
        public const val DEFAULT_BATCH: Int = 20

        private const val HTTP_OK = 200
        private const val HTTP_REDIRECT = 300
    }
}
