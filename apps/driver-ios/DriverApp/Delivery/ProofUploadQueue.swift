import Foundation
import MageRideShared

/// Where one queued proof has got to — `proof_upload_queue.state`'s four values (§3.6).
enum ProofUploadState {

    /// Captured and waiting for its upload.
    case pending

    /// An upload is in flight.
    case uploading

    /// The server has the artifact; the entry can be dropped.
    case uploaded

    /// The upload was refused for a reason a retry will not fix. Kept for the driver to see.
    case failed
}

/// One photograph on its way to `rides.proof_artifacts`.
///
/// A `struct` where `apps/driver-android`'s twin is a hand-written class: that one avoids a generated
/// `equals` because a Kotlin `ByteArray` compares by identity, and Swift's `Data` compares by value, so
/// the reason for the deviation does not exist on this side.
///
/// - Parameters:
///   - id: The entry, so a claim and its outcome name the same row.
///   - rideId: The delivery this is evidence of.
///   - image: The photograph.
///   - capturedAt: When the shutter fired.
///   - at: Where the handset was — `rides.proof_artifacts.captured_geo` (D5' §11). `nil` when there
///     was no fix, which is a photo without a position rather than a refused delivery.
///   - attempts: How many uploads have been tried.
///   - state: Where the entry is.
struct ProofUpload {

    let id: String
    let rideId: String
    let image: CapturedImage
    let capturedAt: Date
    let at: GeoPoint?
    var attempts: Int = 0
    var state: ProofUploadState = .pending
}

/// **P-10's proof-artifact queue** — the delivery photograph, held until the server has it.
///
/// `mobile_db_schema.md` §3.6 gives this queue a durable table (`proof_upload_queue`, kind
/// `delivery_photo`) and `:shared` generates its queries. **This implementation is in memory, and that
/// is a deliberate, narrow deviation** — the same one the C071 handoff records for the Android twin,
/// for the same two reasons:
///
/// 1. **Δ C037 made the photograph the completion, not a filing beside it.**
///    `POST /v1/rides/{rideId}/package/proof-photo` moves the ride `InProgress → Completed`, so an
///    upload deferred to a background drain would be a *delivery* deferred — the driver would walk away
///    from a door with the ride still running. The photo therefore goes up with the action that needs
///    it, which is the same rule C086 landed for document capture.
/// 2. **No app-side database is open yet.** `localDbModule` binds a factory and deliberately not a
///    database (opening it is `suspend`, over the Keychain), and nothing in this target has opened one.
///    Doing it here would be a shell change with a drain worker attached.
///
/// What it *is* for is the case that actually happens at a doorstep: the upload fails on a bad signal
/// and the driver retries **without re-photographing** — losing the picture would mean asking a driver
/// to go back and take it again. The verbs are §3.6's own (``enqueue(rideId:image:at:capturedAt:)``,
/// ``claim(_:)``, ``markUploaded(_:)``, ``reschedule(_:)``, ``markFailed(_:)``, ``discard(rideId:)``) so
/// replacing this with the durable table is a change to this class and to nothing that calls it.
///
/// A process singleton held by ``DriverGraph``: the entry has to outlive the view that captured it,
/// because SCR-DI-005 is a full-screen takeover presented over the delivery sheet.
@MainActor
final class ProofUploadQueue {

    private var entries: [String: ProofUpload] = [:]

    /// Files [image] against [rideId], replacing any earlier photograph for the same delivery.
    @discardableResult
    func enqueue(rideId: String, image: CapturedImage, at: GeoPoint?, capturedAt: Date) -> ProofUpload {
        // One photograph per delivery: re-taking replaces rather than adds, because the second picture
        // is the driver saying the first one was wrong.
        discard(rideId: rideId)

        let millis = Int64((capturedAt.timeIntervalSince1970 * 1000).rounded())
        let entry = ProofUpload(
            id: "\(rideId)-\(image.sizeBytes)-\(millis)",
            rideId: rideId,
            image: image,
            capturedAt: capturedAt,
            at: at
        )
        entries[entry.id] = entry
        return entry
    }

    /// The photograph waiting to be sent for [rideId], whatever state it is in.
    func pending(for rideId: String) -> ProofUpload? {
        entries.values.first { $0.rideId == rideId }
    }

    /// Takes [id] in hand for an upload and counts the attempt. `nil` if it is already gone.
    func claim(_ id: String) -> ProofUpload? {
        guard var entry = entries[id] else { return nil }
        entry.attempts += 1
        entry.state = .uploading
        entries[id] = entry
        return entry
    }

    /// The server has the artifact. The entry is dropped — §4.3 keeps no `uploaded` row.
    func markUploaded(_ id: String) {
        entries.removeValue(forKey: id)
    }

    /// The upload did not get through. The photograph stays, so a retry does not need the camera.
    func reschedule(_ id: String) {
        entries[id]?.state = .pending
    }

    /// The upload was refused for a reason retrying will not fix. Kept, for the same reason.
    func markFailed(_ id: String) {
        entries[id]?.state = .failed
    }

    /// Forgets every photograph held for [rideId] — the delivery ended another way.
    func discard(rideId: String) {
        for entry in entries.values where entry.rideId == rideId {
            entries.removeValue(forKey: entry.id)
        }
    }
}
