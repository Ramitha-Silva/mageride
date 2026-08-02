package lk.mageride.driver.capture

import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * One captured or picked image, in memory.
 *
 * Bytes rather than a `Uri`: what leaves this app is a `multipart/form-data` part
 * ([lk.mageride.shared.data.api.FileUpload] takes a `ByteArray`), and a `Uri` granted to a
 * gallery picker is revoked the moment the process that received it dies — which is exactly when
 * a driver comes back to a half-finished Profile Setup.
 *
 * Not a `data class`: a `ByteArray` compares by identity and a generated `equals` that quietly
 * does the wrong thing is worse than none. Same reasoning as `FileUpload`'s own.
 *
 * @property fileName Name to send in the part's `Content-Disposition`.
 * @property bytes The image, already perspective-corrected when it came from SCR-DA-005.
 * @property mimeType Media type, e.g. `image/jpeg`.
 */
internal class CapturedImage(val fileName: String, val bytes: ByteArray, val mimeType: String = "image/jpeg") {
    /** How many bytes this is, for the size guard on the upload. */
    val sizeBytes: Int get() = bytes.size
}

/**
 * Which slot a capture is filling.
 *
 * The whole set lives here rather than in the screen that opens the camera, because SCR-DA-005 is
 * **one** screen shared by C068's two licence slots and C069's four vehicle-document slots
 * (AL-43). The scanner needs to name the document in its app bar, and that title is a property of
 * the target rather than of whoever navigated.
 */
internal enum class DocumentCaptureTarget {

    /** SCR-DA-003a — front of the driving licence. */
    LICENCE_FRONT,

    /** SCR-DA-003a — back of the driving licence. */
    LICENCE_BACK,

    /** SCR-DA-004a — insurance card or paper (C069). */
    INSURANCE,

    /** SCR-DA-004b — vehicle revenue licence (C069). */
    REVENUE_LICENCE,

    /** SCR-DA-004c — vehicle front, number plate visible (C069). */
    VEHICLE_FRONT,

    /** SCR-DA-004c — vehicle back, number plate visible (C069). */
    VEHICLE_BACK,
}

/** A finished capture, on its way back to the screen that asked for one. */
internal class DocumentCaptureResult(val target: DocumentCaptureTarget, val image: CapturedImage)

/**
 * The seam between a screen with a capture slot and **SCR-DA-005**, the shared camera
 * document-scanner (AL-43, owned by C069).
 *
 * `DriverRoute.DocumentCapture` carries no arguments — the shell fixed the route table before any
 * screen group existed — so "capture the licence front, and give the result back to Profile Setup"
 * cannot be expressed as a navigation argument. It is expressed here instead: the caller
 * [open]s a target, navigates to the route, and collects [results]; the scanner reads [pending],
 * shows the right title, and [deliver]s the cropped image.
 *
 * A process-wide single instance, held by Koin. Two screens cannot be capturing at once — the
 * scanner is a full-screen destination — so one slot is the whole state this needs.
 */
internal class DocumentCaptureCoordinator {

    private val mutablePending = MutableStateFlow<DocumentCaptureTarget?>(null)

    private val mutableResults = MutableSharedFlow<DocumentCaptureResult>(
        replay = 1,
        extraBufferCapacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    /** What SCR-DA-005 has been asked to capture, or `null` when it was opened with no request. */
    val pending: StateFlow<DocumentCaptureTarget?> = mutablePending.asStateFlow()

    /**
     * Finished captures.
     *
     * Replays the last one so a screen that is recomposed — or recreated after the scanner took
     * the foreground — still receives the image it asked for. [consume] clears the replay cache;
     * without it a rotation would re-apply the same capture.
     */
    val results: SharedFlow<DocumentCaptureResult> = mutableResults.asSharedFlow()

    /** Declares what the next visit to SCR-DA-005 is for. Call immediately before navigating. */
    fun open(target: DocumentCaptureTarget) {
        mutablePending.value = target
    }

    /** SCR-DA-005 hands back the cropped, de-skewed image (C069). */
    fun deliver(image: CapturedImage) {
        val target = mutablePending.value ?: return
        mutablePending.value = null
        mutableResults.tryEmit(DocumentCaptureResult(target, image))
    }

    /** The requesting screen has applied the result. Drops the replay so it is not applied twice. */
    fun consume() {
        mutableResults.resetReplayCache()
    }

    /** The driver backed out of the scanner without taking a photo. */
    fun cancel() {
        mutablePending.value = null
    }
}
