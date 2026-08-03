package lk.mageride.driver.delivery

import lk.mageride.driver.capture.CapturedImage
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

/** Where one queued proof has got to — `proof_upload_queue.state`'s four values (§3.6). */
internal enum class ProofUploadState {

    /** Captured and waiting for its upload. */
    PENDING,

    /** An upload is in flight. */
    UPLOADING,

    /** The server has the artifact; the entry can be dropped. */
    UPLOADED,

    /** The upload was refused for a reason a retry will not fix. Kept for the driver to see. */
    FAILED,
}

/**
 * One photograph on its way to `rides.proof_artifacts`.
 *
 * Not a `data class`, for [CapturedImage]'s reason: a `ByteArray` compares by identity and a
 * generated `equals` that quietly does the wrong thing is worse than none.
 *
 * @property id The entry, so a claim and its outcome name the same row.
 * @property rideId The delivery this is evidence of.
 * @property image The photograph.
 * @property capturedAt When the shutter fired.
 * @property at Where the handset was — `rides.proof_artifacts.captured_geo` (D5' §11). `null` when
 *   there was no fix, which is a photo without a position rather than a refused delivery.
 * @property attempts How many uploads have been tried.
 * @property state Where the entry is.
 */
// Seven members, and the count is `proof_upload_queue`'s: §3.6's row carries an id, the ride, the
// file, when and where it was taken, an attempt count and a state, and every one of them is read
// somewhere. The same argument `config/detekt/detekt.yml` makes for a DTO whose constructor is its
// contract's field list.
@Suppress("LongParameterList")
internal class ProofUpload(
    val id: String,
    val rideId: Ulid,
    val image: CapturedImage,
    val capturedAt: Timestamp,
    val at: GeoPoint?,
    val attempts: Int = 0,
    val state: ProofUploadState = ProofUploadState.PENDING,
) {
    internal fun copy(attempts: Int = this.attempts, state: ProofUploadState = this.state): ProofUpload = ProofUpload(
        id = id,
        rideId = rideId,
        image = image,
        capturedAt = capturedAt,
        at = at,
        attempts = attempts,
        state = state,
    )
}

/**
 * **P-10's proof-artifact queue** — the delivery photograph, held until the server has it.
 *
 * `mobile_db_schema.md` §3.6 gives this queue a durable table (`proof_upload_queue`, kind
 * `delivery_photo`) and `:shared` generates its queries. **This implementation is in memory, and
 * that is a deliberate, narrow deviation** for two reasons, both recorded in the C071 handoff:
 *
 * 1. **Δ C037 made the photograph the completion, not a filing beside it.**
 *    `POST /v1/rides/{rideId}/package/proof-photo` moves the ride `InProgress → Completed`, so an
 *    upload deferred to a background drain would be a *delivery* deferred — the driver would walk
 *    away from a door with the ride still running. The photo therefore goes up with the action that
 *    needs it, which is the same rule C068 landed for document capture.
 * 2. **No app-side database is open yet.** `localDbModule` binds a factory and deliberately not a
 *    database (opening it is `suspend`, over the Keystore), and nothing in the Driver App has
 *    opened one. Doing it here would be a shell change with a drain worker attached.
 *
 * What it *is* for is the case that actually happens at a doorstep: the upload fails on a bad
 * signal, and the driver retries **without re-photographing** — losing the picture would mean
 * asking a driver to go back and take it again. The verbs are §3.6's own ([enqueue], [claim],
 * [markUploaded], [reschedule], [markFailed], [discard]) so replacing this with the durable table
 * is a change to this class and to nothing that calls it.
 *
 * A process singleton: the entry has to outlive the composition that captured it, because the
 * scanner is a full-screen destination and the delivery sheet is not composed while it is up.
 */
internal class ProofUploadQueue {

    private val entries = mutableMapOf<String, ProofUpload>()

    /** Files [image] against [rideId], replacing any earlier photograph for the same delivery. */
    fun enqueue(rideId: Ulid, image: CapturedImage, at: GeoPoint?, capturedAt: Timestamp): ProofUpload {
        // One photograph per delivery: re-taking replaces rather than adds, because the second
        // picture is the driver saying the first one was wrong.
        discard(rideId)

        val entry = ProofUpload(
            id = "$rideId-${image.sizeBytes}-${capturedAt.toEpochMilliseconds()}",
            rideId = rideId,
            image = image,
            capturedAt = capturedAt,
            at = at,
        )
        entries[entry.id] = entry
        return entry
    }

    /** The photograph waiting to be sent for [rideId], whatever state it is in. */
    fun pendingFor(rideId: Ulid): ProofUpload? = entries.values.firstOrNull { it.rideId == rideId }

    /** Takes [id] in hand for an upload and counts the attempt. `null` if it is already gone. */
    fun claim(id: String): ProofUpload? {
        val entry = entries[id] ?: return null
        val claimed = entry.copy(attempts = entry.attempts + 1, state = ProofUploadState.UPLOADING)
        entries[id] = claimed
        return claimed
    }

    /** The server has the artifact. The entry is dropped — §4.3 keeps no `UPLOADED` row. */
    fun markUploaded(id: String) {
        entries.remove(id)
    }

    /** The upload did not get through. The photograph stays, so a retry does not need the camera. */
    fun reschedule(id: String) {
        entries[id]?.let { entries[it.id] = it.copy(state = ProofUploadState.PENDING) }
    }

    /** The upload was refused for a reason retrying will not fix. Kept, for the same reason. */
    fun markFailed(id: String) {
        entries[id]?.let { entries[it.id] = it.copy(state = ProofUploadState.FAILED) }
    }

    /** Forgets every photograph held for [rideId] — the delivery ended another way. */
    fun discard(rideId: Ulid) {
        entries.values.filter { it.rideId == rideId }.forEach { entries.remove(it.id) }
    }
}
