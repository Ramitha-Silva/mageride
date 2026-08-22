package lk.mageride.driver.capture

import android.Manifest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.StringRes
import androidx.camera.core.ImageCapture
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.FlashOff
import androidx.compose.material.icons.outlined.FlashOn
import androidx.compose.material.icons.outlined.PhotoLibrary
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.ClipOp
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.IntSize
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.launch
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.MageRideCta
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.driver.ui.theme.ScannerColors
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-005 · document capture (camera + drag-crop)** — the shared scanner (AL-43).
 *
 * The wireframe top to bottom: a dark screen with a ✕ / *"Capture: Licence front"* / `⚡ Flash`
 * bar, the viewfinder with the crop quadrilateral, its four corner handles and a rule-of-thirds
 * grid, the hint *"Drag the corners so the whole document fills the frame"*, and the
 * `Retake · ◉ · Use photo ›` bar under it.
 *
 * **Two modes, decided by `DocumentCaptureTarget.isDocument`.** A document gets the rear lens, the
 * crop box once the still is taken, and the gallery fallback if the camera is refused. A profile
 * photo (SCR-DA-003a) gets the selfie lens, no crop box, and no gallery — the picture exists to
 * show a passenger who is driving them, and a file already on the handset says nothing about that.
 *
 * **The frame is 4:3 and the viewfinder is letterboxed inside it on purpose.** The crop quad is
 * stored in normalised coordinates and applied to the *captured still*, so the preview and the
 * capture have to be the same rectangle — a `FILL_CENTER` viewfinder crops the sides away, and a
 * corner the driver put on the edge of what they could see would land somewhere else entirely in
 * the file that gets uploaded.
 *
 * @param onFinished Pops back to the screen that asked for the capture. The result reaches it
 *   through [DocumentCaptureCoordinator], not through a navigation argument — the route has none.
 */
@Composable
internal fun DocumentScannerScreen(onFinished: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: DocumentScannerViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    // One use case for the screen: the viewfinder binds it and the shutter fires it, and two
    // instances would mean a shutter pointed at a camera nothing is showing.
    val imageCapture = remember { newImageCapture() }

    var granted by remember { mutableStateOf(context.hasCameraPermission()) }
    val requestCamera = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        granted = it
    }
    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        val target = state.target
        if (uri != null && target != null) {
            scope.launch { readImage(context, uri, target.fileName)?.let(viewModel::onPicked) }
        }
    }

    LaunchedEffect(state.done) {
        if (state.done) onFinished()
    }

    // Opened with nothing pending — a restore after the requesting screen was destroyed, or a
    // navigation nobody declared a target for. There is nothing to photograph *for*, so leave.
    LaunchedEffect(state.target) {
        if (state.target == null) onFinished()
    }

    LaunchedEffect(granted) {
        if (!granted) requestCamera.launch(Manifest.permission.CAMERA)
    }

    Column(
        modifier = modifier
            .fillMaxSize()
            .background(ScannerColors.background),
    ) {
        ScannerBar(
            title = state.target?.let { stringResource(R.string.capture_title, stringResource(it.labelRes())) }
                ?: stringResource(R.string.capture_title_generic),
            torchOn = state.torchOn,
            onClose = viewModel::cancel,
            onToggleTorch = viewModel::toggleTorch,
        )

        Box(
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth(),
            contentAlignment = Alignment.Center,
        ) {
            if (granted || state.isReviewing) {
                Viewfinder(
                    state = state,
                    imageCapture = imageCapture,
                    onCornerDragged = viewModel::onCornerDragged,
                )
            } else {
                CameraDenied(
                    onAllow = { requestCamera.launch(Manifest.permission.CAMERA) },
                    // A profile photo has no gallery way out on purpose (US-2.12): the point of
                    // the picture is that this driver was in front of this camera, and a file
                    // already on the handset proves the opposite of that.
                    onPickFromGallery = if (state.isDocument) {
                        {
                            pickImage.launch(
                                PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly),
                            )
                        }
                    } else {
                        null
                    },
                )
            }

            if (state.busy) {
                CircularProgressIndicator(color = ScannerColors.accent)
            }
        }

        state.error?.let { message ->
            Text(
                text = stringResource(message),
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = MageRideTheme.spacing.md),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.error,
                textAlign = TextAlign.Center,
            )
        }

        Text(
            text = stringResource(hintFor(state)),
            modifier = Modifier
                .fillMaxWidth()
                .padding(MageRideTheme.spacing.sm),
            style = MaterialTheme.typography.labelSmall,
            color = ScannerColors.hint,
            textAlign = TextAlign.Center,
        )

        CaptureBar(
            reviewing = state.isReviewing,
            enabled = !state.busy,
            onRetake = viewModel::retake,
            onShutter = { scope.launch { imageCapture.deliverTo(viewModel) } },
            onConfirm = viewModel::confirm,
        )
    }
}

/** The wireframe's dark app bar: ✕, what is being captured, and the flash toggle. */
@Composable
private fun ScannerBar(
    title: String,
    torchOn: Boolean,
    onClose: () -> Unit,
    onToggleTorch: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconButton(onClick = onClose) {
            Icon(
                imageVector = Icons.Outlined.Close,
                contentDescription = stringResource(R.string.action_close),
                tint = Color.White,
            )
        }
        Text(
            text = title,
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.titleMedium,
            color = Color.White,
        )
        TextButton(onClick = onToggleTorch) {
            Icon(
                imageVector = if (torchOn) Icons.Outlined.FlashOn else Icons.Outlined.FlashOff,
                contentDescription = null,
                modifier = Modifier.size(ControlTokens.ChipIcon),
                tint = ScannerColors.accent,
            )
            Text(
                text = stringResource(R.string.capture_flash),
                style = MaterialTheme.typography.labelSmall,
                color = ScannerColors.accent,
            )
        }
    }
}

/**
 * The 4:3 frame — the live camera or the still under review — with the crop overlay on top.
 *
 * One `Box` for both so the overlay's normalised coordinates mean the same thing in either state.
 */
@Composable
private fun Viewfinder(
    state: DocumentScannerState,
    imageCapture: ImageCapture,
    onCornerDragged: (CropCorner, QuadPoint) -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            .fillMaxWidth()
            .aspectRatio(FRAME_ASPECT),
        contentAlignment = Alignment.Center,
    ) {
        val captured = state.captured
        if (captured != null) {
            Image(
                bitmap = captured.asImageBitmap(),
                contentDescription = null,
                modifier = Modifier.fillMaxSize(),
                contentScale = ContentScale.Fit,
            )
        } else {
            CameraPreview(
                torchOn = state.torchOn,
                imageCapture = imageCapture,
                frontFacing = !state.isDocument,
            )
        }

        // The crop box is the document scanner's, and only once there is a still to crop. Over a
        // live preview it invites a drag that the next frame throws away; over a face it asks for
        // a rectangle that has no right answer.
        if (state.isDocument && state.isReviewing) {
            CropOverlay(quad = state.quad, onCornerDragged = onCornerDragged)
        }
    }
}

/**
 * The wireframe's `crop` quad: a scrim outside it, a rule-of-thirds grid inside, and four corner
 * handles a thumb can find.
 *
 * The drag picks the **nearest corner within a thumb's reach** on touch-down and keeps it for the
 * whole gesture. Re-picking mid-drag is what makes two handles swap when they are dragged close
 * together, and a quad whose corners have swapped is the folded one [CropQuad] refuses.
 */
@Composable
private fun CropOverlay(
    quad: CropQuad,
    onCornerDragged: (CropCorner, QuadPoint) -> Unit,
    modifier: Modifier = Modifier,
) {
    val current by rememberUpdatedState(quad)

    // Δ MCS-22 — the CALLBACK gets the same protection the quad already had, and the asymmetry
    // between the two was a trap rather than a bug. `pointerInput(Unit)` below never restarts, so
    // the block captures whatever `onCornerDragged` instance existed when the node was created and
    // holds it for the node's life. It is harmless today only by accident: every instance is bound
    // to the same view model, because `koinViewModel()` returns one per destination. Hoist this
    // callback, or move the view model up a level, and the handles freeze for real — which is
    // indistinguishable from the defect this file was just fixed for.
    val onDrag by rememberUpdatedState(onCornerDragged)

    var active by remember { mutableStateOf<CropCorner?>(null) }

    Canvas(
        modifier = modifier
            .fillMaxSize()
            .pointerInput(Unit) {
                detectDragGestures(
                    onDragStart = { offset -> active = nearestCorner(current, offset, size) },
                    onDragEnd = { active = null },
                    onDragCancel = { active = null },
                    onDrag = { change, _ ->
                        change.consume()
                        active?.let { corner ->
                            onDrag(
                                corner,
                                QuadPoint(change.position.x / size.width, change.position.y / size.height),
                            )
                        }
                    },
                )
            },
    ) {
        val path = Path().apply {
            val points = current.corners
            moveTo(points[0].x * size.width, points[0].y * size.height)
            points.drop(1).forEach { lineTo(it.x * size.width, it.y * size.height) }
            close()
        }

        clipPath(path, ClipOp.Difference) { drawRect(color = Color.Black.copy(alpha = SCRIM_ALPHA)) }
        drawPath(path, color = ScannerColors.accent, style = Stroke(width = ControlTokens.ScannerQuadStroke.toPx()))
        drawThirds(current)

        current.corners.forEach { point ->
            drawCircle(
                color = ScannerColors.accent,
                radius = ControlTokens.ScannerHandle.toPx(),
                center = Offset(point.x * size.width, point.y * size.height),
            )
        }
    }
}

/**
 * The rule-of-thirds grid, drawn **inside the quad** rather than across the frame.
 *
 * Interpolating along the quad's own edges is what makes the grid follow the perspective: lines
 * across a rectangle over a skewed document would tell the driver the document was straight.
 */
private fun DrawScope.drawThirds(quad: CropQuad) {
    fun between(from: QuadPoint, to: QuadPoint, fraction: Float): Offset = Offset(
        x = (from.x + (to.x - from.x) * fraction) * size.width,
        y = (from.y + (to.y - from.y) * fraction) * size.height,
    )

    listOf(THIRD, TWO_THIRDS).forEach { fraction ->
        drawLine(
            color = ScannerColors.accent.copy(alpha = GRID_ALPHA),
            start = between(quad.topLeft, quad.topRight, fraction),
            end = between(quad.bottomLeft, quad.bottomRight, fraction),
            strokeWidth = ControlTokens.ScannerGridStroke.toPx(),
        )
        drawLine(
            color = ScannerColors.accent.copy(alpha = GRID_ALPHA),
            start = between(quad.topLeft, quad.bottomLeft, fraction),
            end = between(quad.topRight, quad.bottomRight, fraction),
            strokeWidth = ControlTokens.ScannerGridStroke.toPx(),
        )
    }
}

/**
 * The instruction under the viewfinder — **two beats, two instructions**.
 *
 * Telling a driver to drag corners while the viewfinder is still live asks them to adjust a box
 * over a picture that has not been taken: the next frame throws the drag away, and the crop only
 * becomes real once there is a still under it. So the hint says *take it* first and *trim it*
 * after, and for a face it says neither — there is no box on that one.
 */
@StringRes
private fun hintFor(state: DocumentScannerState): Int = when {
    !state.isDocument && state.isReviewing -> R.string.capture_hint_face_review
    !state.isDocument -> R.string.capture_hint_face
    state.isReviewing -> R.string.capture_hint_crop
    else -> R.string.capture_hint_shoot
}

/**
 * The corner nearest [touch]. Never `null` (Δ MCS-22).
 *
 * **This function is why the handles were dead on the licence BACK and alive on the front.**
 *
 * It used to filter to corners within `frame.width * HANDLE_REACH` and answer `null` when the
 * touch-down landed near none — and [CropOverlay]'s `onDrag` reads `active?.let { … }` AFTER
 * `change.consume()`. So a touch-down that missed the reach did not fall through to the parent, and
 * did not start a drag either: every move event for the whole gesture was swallowed in silence. No
 * state change, no redraw, no haptic. Lift, press again, same. That is exactly "the corners do not
 * drag", and it was the only path in this screen that produces it with the handles still drawn.
 *
 * There is no per-target branch anywhere in this file, which is what made the front/back asymmetry
 * look impossible. It is not the target — it is the PROPOSAL. `DocumentEdgeDetector` reads the
 * reverse of a licence, which is a dense class table running edge to edge, and proposes a quad much
 * closer to the frame border than the sparser front does. `CropQuad.DEFAULT` is inset precisely so
 * "every handle can be reached" (its own KDoc), and the detector's output was never held to that
 * rule — so the back's corners could sit a few percent from the edge, under the system
 * back-gesture strip, outside a reach that is itself a fraction of a frame width.
 *
 * A drag now always grabs the closest corner, and there is no reach test left to fail. This
 * overlay has exactly one gesture — there is no pan, no pinch and nothing else a touch could have
 * been meant for — so a drag the driver started is unambiguously a drag of the nearest handle.
 * That also retires a second latent trap: `HANDLE_REACH` was a fraction of a frame width, and this
 * screen declares no orientation, so in landscape the fixed 3:4 frame narrows to roughly 40% of
 * its portrait width and took the reach down with it — below the 48 dp Material minimum, on the
 * axis where a thumb is least precise.
 */
private fun nearestCorner(quad: CropQuad, touch: Offset, frame: IntSize): CropCorner =
    CropCorner.entries
        .minBy { corner ->
            val point = quad.corner(corner)
            (Offset(point.x * frame.width, point.y * frame.height) - touch).getDistance()
        }

/** The wireframe's `capbar` — `Retake · ◉ · Use photo ›`. */
@Composable
private fun CaptureBar(
    reviewing: Boolean,
    enabled: Boolean,
    onRetake: () -> Unit,
    onShutter: () -> Unit,
    onConfirm: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(MageRideTheme.spacing.md),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        TextButton(onClick = onRetake, enabled = reviewing && enabled) {
            Text(
                text = stringResource(R.string.capture_retake),
                style = MaterialTheme.typography.titleMedium,
                color = if (reviewing && enabled) Color.White else ScannerColors.hint,
            )
        }
        Box(
            modifier = Modifier
                .size(ControlTokens.Shutter)
                .background(if (reviewing) ScannerColors.hint else Color.White, CircleShape)
                .clickable(enabled = !reviewing && enabled, onClick = onShutter),
        )
        TextButton(onClick = onConfirm, enabled = reviewing && enabled) {
            Text(
                text = stringResource(R.string.capture_use_photo),
                style = MaterialTheme.typography.titleMedium,
                color = if (reviewing && enabled) ScannerColors.accent else ScannerColors.hint,
            )
        }
    }
}

/**
 * The wireframe's *"Permission-denied → Allow camera prompt"*, with the gallery way out.
 *
 * The gallery is a **fallback**, not a peer: `readImage` stamps what it returns
 * `CaptureSource.GALLERY`, which AL-43 makes the signal the Verification-Officer queue sorts on.
 * Offering it anyway is what keeps a handset with a broken camera onboardable at all.
 */
@Composable
private fun CameraDenied(onAllow: () -> Unit, onPickFromGallery: (() -> Unit)?, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(MageRideTheme.spacing.lg),
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.capture_permission_title),
            style = MaterialTheme.typography.titleLarge,
            color = Color.White,
            textAlign = TextAlign.Center,
        )
        Text(
            text = stringResource(R.string.capture_permission_body),
            style = MaterialTheme.typography.bodyMedium,
            color = ScannerColors.hint,
            textAlign = TextAlign.Center,
        )
        MageRideCta(label = stringResource(R.string.capture_permission_allow), onClick = onAllow)
        if (onPickFromGallery != null) {
            TextButton(onClick = onPickFromGallery) {
                Icon(
                    imageVector = Icons.Outlined.PhotoLibrary,
                    contentDescription = null,
                    modifier = Modifier.size(ControlTokens.ChipIcon),
                    tint = ScannerColors.accent,
                )
                Text(
                    text = stringResource(R.string.capture_from_gallery),
                    style = MaterialTheme.typography.labelLarge,
                    color = ScannerColors.accent,
                )
            }
        }
    }
}

private const val FRAME_ASPECT = 3f / 4f
private const val SCRIM_ALPHA = 0.55f
private const val GRID_ALPHA = 0.4f
private const val THIRD = 1f / 3f
private const val TWO_THIRDS = 2f / 3f
