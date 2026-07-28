package lk.mageride.shared.db

import kotlin.time.Instant

// The §4.2 read path — cache + reconcile.
//
// "On foreground / pull-to-refresh / push wake, the app fetches deltas (cursor in
// meta.sync.cursor.*) and upserts projections, setting synced_at and server_updated_at."
//
// Conflict rule, verbatim: "if a row is dirty=1 and the server updated_at is newer, the pending
// command_outbox entry is authoritative until ACKed; otherwise last-writer-wins on
// server_updated_at."

/** What [reconcile] decided about one incoming row. */
public enum class ReconcileDecision {
    /** Overwrite the local projection with the server's row. */
    APPLY_SERVER,

    /**
     * Leave the local projection alone.
     *
     * Either an unACKed local edit owns the row, or the server's copy is not newer than the one
     * already applied — re-writing it would churn the UI for nothing.
     */
    KEEP_LOCAL,
}

/**
 * The local row's state, as far as the conflict rule cares.
 *
 * @property dirty §0.5's flag — a local edit is pending upload.
 * @property hasPendingCommand Whether `command_outbox` still holds an unACKed row for this
 *   projection ([CommandOutbox.pendingFor]).
 * @property serverUpdatedAt The server `updated_at` this projection was last built from, or
 *   `null` if it has never been reconciled.
 */
public data class LocalRowState(val dirty: Boolean, val hasPendingCommand: Boolean, val serverUpdatedAt: Instant?)

/**
 * Applies §4.2's conflict rule to one incoming server row.
 *
 * Two clauses, in this order:
 *
 * 1. **A dirty row with a live outbox command wins**, no matter how new the server's copy is. The
 *    user tapped something, the app showed them the result optimistically, and the command has
 *    not been answered yet — overwriting now would make their edit visibly vanish and then
 *    reappear when the ACK lands.
 * 2. **Otherwise last-writer-wins on `server_updated_at`.** Strictly newer, so a re-fetch of the
 *    same version is a no-op.
 *
 * The interesting case is `dirty = true` with **no** pending command: the command was ACKed,
 * failed, or the pair was broken by a crash between the two writes §4.1 asks for in one
 * transaction. Clause 1 does not apply and the server wins — which is the only outcome that
 * clears a stranded `dirty` flag instead of pinning the row on stale local data forever.
 */
public fun reconcile(local: LocalRowState, serverUpdatedAt: Instant?): ReconcileDecision = when {
    local.dirty && local.hasPendingCommand -> ReconcileDecision.KEEP_LOCAL
    serverUpdatedAt == null -> ReconcileDecision.KEEP_LOCAL
    local.serverUpdatedAt == null -> ReconcileDecision.APPLY_SERVER
    serverUpdatedAt > local.serverUpdatedAt -> ReconcileDecision.APPLY_SERVER
    else -> ReconcileDecision.KEEP_LOCAL
}
