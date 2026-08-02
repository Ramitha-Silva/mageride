package lk.mageride.driver.capture

import android.Manifest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
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
                    onPickFromGallery = {
                        pickImage.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
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
            text = stringResource(R.string.capture_hint),
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
            CameraPreview(torchOn = state.torchOn, imageCapture = imageCapture)
        }

        CropOverlay(quad = state.quad, onCornerDragged = onCornerDragged)
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
                            onCornerDragged(
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

/** The corner nearest [touch], or `null` when the touch landed on none of them. */
private fun nearestCorner(quad: CropQuad, touch: Offset, frame: IntSize): CropCorner? {
    val reach = frame.width * HANDLE_REACH
    return CropCorner.entries
        .map { corner ->
            val point = quad.corner(corner)
            corner to (Offset(point.x * frame.width, point.y * frame.height) - touch).getDistance()
        }
        .filter { (_, distance) -> distance <= reach }
        .minByOrNull { (_, distance) -> distance }
        ?.first
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
private fun CameraDenied(onAllow: () -> Unit, onPickFromGallery: () -> Unit, modifier: Modifier = Modifier) {
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

private const val FRAME_ASPECT = 3f / 4f
private const val SCRIM_ALPHA = 0.55f
private const val GRID_ALPHA = 0.4f
private const val THIRD = 1f / 3f
private const val TWO_THIRDS = 2f / 3f
private const val HANDLE_REACH = 0.14f
