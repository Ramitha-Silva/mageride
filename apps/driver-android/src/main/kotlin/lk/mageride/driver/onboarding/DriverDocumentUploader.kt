package lk.mageride.driver.onboarding

import lk.mageride.driver.capture.CapturedImage
import lk.mageride.shared.data.models.Ulid

/**
 * Which `docs.uploads` slot an image is being stored for.
 *
 * Profile Setup's three (AL-27). The vehicle-onboarding documents do **not** appear here: those go
 * up inside `PUT /v1/vehicles/{id}/onboarding/{step}`, which takes the bytes and the step's fields
 * in one multipart request (C069's path), so they never need a standalone upload.
 */
internal enum class DriverDocumentKind {

    /** The profile photo passengers see. Required (US-2.12). */
    PROFILE_PHOTO,

    /** Front of the driving licence. */
    LICENCE_FRONT,

    /** Back of the driving licence. */
    LICENCE_BACK,
}

/**
 * Stores one captured image and answers with the `docs.uploads` id `PUT /v1/drivers/profile`
 * expects.
 *
 * ### There is no route behind this yet — and that is a platform gap, not a design choice
 *
 * `registry.yaml`'s `upsertDriverProfile` takes `profilePhotoFileId`, `licenseFrontFileId` and
 * `licenseBackFileId` as **already-uploaded** ids, and registry-svc's `RequireUploadAsync` rejects
 * an id that is not on file. No contract in `backend/contracts` exposes a route that would create
 * one for these three kinds: the eight `upload*` operations cover payout documents, fleet
 * documents, package proof, transfer slips, support screenshots and the GTFS feed, and the
 * vehicle-onboarding multipart is vehicle-scoped. `ocr.yaml` says so in its own header —
 * *"Filling `docs.uploads` for onboarding is still unowned"*.
 *
 * So this interface is the shape of the call the moment that route lands, and
 * [UnavailableDriverDocumentUploader] is the honest implementation until then: it fails loudly at
 * the one point that cannot work, rather than letting Profile Setup post three ids the server will
 * refuse. Raised as a micro-change-set in the C068 handoff.
 */
internal fun interface DriverDocumentUploader {

    /** Uploads [image] and returns its `docs.uploads` id. */
    suspend fun upload(kind: DriverDocumentKind, image: CapturedImage): Ulid
}

/**
 * Thrown when a Profile Setup document cannot be uploaded because the platform has no route for
 * it. Distinct from a network failure: retrying will not help, and the screen says something
 * different.
 */
internal class DocumentUploadUnavailableException :
    IllegalStateException("no docs.uploads route exists for driver profile documents")

/**
 * The binding until the upload route lands. Always fails, and names why.
 *
 * Deliberately not a stub that invents an id: a fabricated `docs.uploads` id would turn a missing
 * endpoint into a `404` from `PUT /v1/drivers/profile` — the same outcome, one layer further from
 * the cause, and a driver would see "something went wrong" instead of the truth.
 */
internal class UnavailableDriverDocumentUploader : DriverDocumentUploader {
    override suspend fun upload(kind: DriverDocumentKind, image: CapturedImage): Ulid =
        throw DocumentUploadUnavailableException()
}
