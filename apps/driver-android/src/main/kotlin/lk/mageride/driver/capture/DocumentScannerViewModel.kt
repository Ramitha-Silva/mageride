package lk.mageride.driver.capture

import android.graphics.Bitmap
import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.R
import lk.mageride.shared.data.models.registry.CaptureSource

/**
 * SCR-DA-005's state.
 *
 * @property target What this visit to the scanner is for; `null` when it was opened with no
 *   pending request, which is a state that should close rather than photograph anything.
 * @property captured The still under review. `null` while the viewfinder is live — the two are the
 *   same screen in the wireframe, and this is what tells them apart.
 * @property quad The crop quadrilateral, proposed by [DocumentEdgeDetector] and dragged by the
 *   driver (AL-43).
 * @property torchOn Whether the flash is lit — the wireframe's `⚡ Flash`, for the low-light case
 *   its own states line calls out.
 * @property busy A decode or a de-skew is running.
 * @property error Resolved copy for the last failure.
 * @property done The image has been delivered; the screen pops back to whoever asked for it.
 */
internal data class DocumentScannerState(
    val target: DocumentCaptureTarget? = null,
    val captured: Bitmap? = null,
    val quad: CropQuad = CropQuad.DEFAULT,
    val torchOn: Boolean = false,
    val busy: Boolean = false,
    @param:StringRes val error: Int? = null,
    val done: Boolean = false,
) {
    /** Whether the shutter has fired and the driver is on `Retake / Use photo`. */
    val isReviewing: Boolean get() = captured != null
}

/**
 * **SCR-DA-005 · document capture** — the one camera document-scanner every onboarding image goes
 * through (AL-43, BR-28.4).
 *
 * Shared by C068's two licence slots and C069's four vehicle-document slots, which is why nothing
 * here knows what a licence or an insurance card *is*: [DocumentCaptureCoordinator] holds the
 * pending [DocumentCaptureTarget], this screen photographs it, and the screen that asked collects
 * the result. The route carries no arguments, so the coordinator is the only way to say what a
 * capture is for.
 *
 * The sequence the wireframe draws, and the reason it is in that order:
 *
 * 1. **Live** viewfinder with the crop quad over it — the driver frames the document.
 * 2. **Shutter** → the still is decoded and the quad is re-proposed from the frame that was
 *    *actually taken*, which is not the one that was on screen when the thumb moved.
 * 3. **Drag** the four handles. The proposal is only ever a proposal (BR-28.4).
 * 4. **Use photo** → perspective-correct, de-skew, hand back. **Retake** → step 1.
 *
 * The de-skew happens on confirm and not on capture because it is the expensive half and a driver
 * who is going to retake should not pay for it.
 */
internal class DocumentScannerViewModel(private val captures: DocumentCaptureCoordinator) : ViewModel() {

    private val mutableState = MutableStateFlow(DocumentScannerState(target = captures.pending.value))

    val state: StateFlow<DocumentScannerState> = mutableState.asStateFlow()

    /** The `⚡ Flash` toggle. */
    fun toggleTorch() {
        mutableState.update { it.copy(torchOn = !it.torchOn) }
    }

    /**
     * The shutter fired.
     *
     * @param rotationDegrees `ImageInfo.rotationDegrees` — a handset held sideways captures
     *   sideways, and a document rotated 90° is one Gemini Flash reads as nothing at all.
     */
    fun onCaptured(bytes: ByteArray, rotationDegrees: Int) {
        mutableState.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            val bitmap = DocumentImaging.decode(bytes, rotationDegrees)
            if (bitmap == null) {
                mutableState.update { it.copy(busy = false, error = R.string.capture_failed) }
                return@launch
            }
            val proposal = DocumentEdgeDetector.propose(DocumentImaging.luminanceGrid(bitmap)) ?: CropQuad.DEFAULT
            mutableState.update { current ->
                current.captured?.recycle()
                current.copy(captured = bitmap, quad = proposal, busy = false)
            }
        }
    }

    /** CameraX could not take the picture — a hardware failure, not a driver error. */
    fun onCaptureFailed() {
        mutableState.update { it.copy(busy = false, error = R.string.capture_failed) }
    }

    /** A corner handle was dragged. [CropQuad] is what refuses a move that folds the quad. */
    fun onCornerDragged(corner: CropCorner, to: QuadPoint) {
        mutableState.update { it.copy(quad = it.quad.moved(corner, to)) }
    }

    /** `Retake` — drop the still and go back to the viewfinder. */
    fun retake() {
        mutableState.update { current ->
            current.captured?.recycle()
            current.copy(captured = null, quad = CropQuad.DEFAULT, error = null)
        }
    }

    /**
     * `Use photo ›` — perspective-correct the quad and hand the result back.
     *
     * The image is stamped [CaptureSource.CAMERA_DRAG_CROP] here and nowhere else: AL-43 makes the
     * capture route a fraud signal the Verification-Officer queue sorts on, so the only thing
     * allowed to claim a scan happened is the code that performed one.
     */
    @Suppress("TooGenericExceptionCaught")
    fun confirm() {
        val current = mutableState.value
        val bitmap = current.captured
        val target = current.target
        if (bitmap == null || target == null || current.busy) return

        mutableState.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                val bytes = DocumentImaging.deskew(bitmap, current.quad)
                if (bytes == null) {
                    mutableState.update { it.copy(busy = false, error = R.string.capture_failed) }
                } else {
                    captures.deliver(
                        CapturedImage(
                            fileName = target.fileName,
                            bytes = bytes,
                            capturedVia = CaptureSource.CAMERA_DRAG_CROP,
                        ),
                    )
                    mutableState.update { it.copy(busy = false, done = true) }
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                mutableState.update { it.copy(busy = false, error = R.string.capture_failed) }
            }
        }
    }

    /**
     * The gallery fallback, for a handset whose camera is denied or broken.
     *
     * It is a fallback and not a peer: the picked image arrives already stamped
     * [CaptureSource.GALLERY] by `readImage`, because a file already on the handset is how a
     * document belonging to somebody else arrives (AL-43) — and that is exactly what the officer
     * queue wants to know.
     */
    fun onPicked(image: CapturedImage) {
        if (mutableState.value.target == null) return
        captures.deliver(image)
        mutableState.update { it.copy(done = true) }
    }

    /** The ✕ in the app bar. The slot stays empty and the requesting screen is untouched. */
    fun cancel() {
        captures.cancel()
        mutableState.update { it.copy(done = true) }
    }

    override fun onCleared() {
        mutableState.value.captured?.recycle()
        super.onCleared()
    }
}

/**
 * The name this slot's image is uploaded under.
 *
 * `docs.uploads` keeps the original file name, and "insurance.jpg" in the Verification Officer's
 * document viewer is the difference between a queue they can work and eight identical rows called
 * `IMG_0042`.
 */
internal val DocumentCaptureTarget.fileName: String
    get() = when (this) {
        DocumentCaptureTarget.LICENCE_FRONT -> "licence-front.jpg"
        DocumentCaptureTarget.LICENCE_BACK -> "licence-back.jpg"
        DocumentCaptureTarget.INSURANCE -> "insurance.jpg"
        DocumentCaptureTarget.REVENUE_LICENCE -> "revenue-licence.jpg"
        DocumentCaptureTarget.VEHICLE_FRONT -> "vehicle-front.jpg"
        DocumentCaptureTarget.VEHICLE_BACK -> "vehicle-back.jpg"
        DocumentCaptureTarget.DELIVERY_PROOF -> "delivery-proof.jpg"
    }

/** The trilingual name of what is being captured — the wireframe's `Capture: Licence front`. */
@StringRes
internal fun DocumentCaptureTarget.labelRes(): Int = when (this) {
    DocumentCaptureTarget.LICENCE_FRONT -> R.string.capture_target_licence_front
    DocumentCaptureTarget.LICENCE_BACK -> R.string.capture_target_licence_back
    DocumentCaptureTarget.INSURANCE -> R.string.capture_target_insurance
    DocumentCaptureTarget.REVENUE_LICENCE -> R.string.capture_target_revenue_licence
    DocumentCaptureTarget.VEHICLE_FRONT -> R.string.capture_target_vehicle_front
    DocumentCaptureTarget.VEHICLE_BACK -> R.string.capture_target_vehicle_back
    DocumentCaptureTarget.DELIVERY_PROOF -> R.string.capture_target_delivery_proof
}
