package lk.mageride.driver.tracker

import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.google.zxing.BinaryBitmap
import com.google.zxing.ChecksumException
import com.google.zxing.DecodeHintType
import com.google.zxing.FormatException
import com.google.zxing.NotFoundException
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import lk.mageride.driver.R
import lk.mageride.driver.capture.cameraProvider
import lk.mageride.driver.capture.hasCameraPermission
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.driver.ui.theme.ScannerColors
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * SCR-DA-027's **▣ Scan device QR** — a full-screen viewfinder that answers with the payload.
 *
 * **A `Dialog`, not a destination.** SCR-DA-005's document scanner is a route because the screen
 * that asked for a capture is not composed while the camera is up, and a 3 MB image has to survive
 * that; a QR read is a short string that goes straight back to the view model underneath, so a
 * route, an argument-less coordinator and a third entry in the navigation graph would all be
 * machinery for nothing. Same reasoning that makes SCR-DA-014's offer a `Dialog` (C070).
 *
 * **ZXing, not ML Kit.** D2' §SCR-DA-027's component table says *"CameraX + ML Kit"*. The CameraX
 * half is exactly that; the decoder is `com.google.zxing:core`, which is **already** a dependency
 * of this module — C073 added it for AL-15's LankaQR rendering and used only its writer. ML Kit
 * would add a Play-services-backed dependency and a downloaded model to read fifteen digits off a
 * sticker. Recorded as a deviation in the C074 handoff.
 *
 * @param onScanned The decoded payload. [TrackerImei.imeiIn] is what turns it into an IMEI — this
 *   composable deliberately knows nothing about what a device QR contains.
 */
@Composable
internal fun DeviceQrScannerDialog(onScanned: (String) -> Unit, onDismiss: () -> Unit) {
    Dialog(
        onDismissRequest = onDismiss,
        // The viewfinder IS the screen while it is up; the default dialog width would put a camera
        // preview inside a card.
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(ScannerColors.background),
            contentAlignment = Alignment.Center,
        ) {
            val context = LocalContext.current
            if (context.hasCameraPermission()) {
                QrViewfinder(onScanned = onScanned)
            } else {
                // SCR-DA-007 is where the grant is asked for with a rationale (C068). A denial here
                // is a state, never a crash, and the IMEI field is still there to type into.
                Text(
                    text = stringResource(R.string.tracker_scan_no_camera),
                    modifier = Modifier.padding(MageRideTheme.spacing.lg),
                    style = MaterialTheme.typography.bodyMedium,
                    color = ScannerColors.hint,
                    textAlign = TextAlign.Center,
                )
            }

            ScannerFooter(onDismiss = onDismiss, modifier = Modifier.align(Alignment.BottomCenter))
        }
    }
}

/** The aiming hint and the way out. */
@Composable
private fun ScannerFooter(onDismiss: () -> Unit, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.padding(MageRideTheme.spacing.lg),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = stringResource(R.string.tracker_scan_hint),
            style = MaterialTheme.typography.bodyMedium,
            color = ScannerColors.hint,
            textAlign = TextAlign.Center,
        )
        TextButton(onClick = onDismiss) {
            Text(text = stringResource(R.string.action_cancel), color = ScannerColors.accent)
        }
    }
}

/**
 * The preview, with an `ImageAnalysis` decoding the frames it is handed.
 *
 * `STRATEGY_KEEP_ONLY_LATEST` because a QR code is a state of the world rather than a stream of
 * events: dropping frames while one is being decoded reads the same code a moment later, whereas
 * queueing them would work through a backlog of identical frames on a budget handset.
 */
@Composable
private fun QrViewfinder(onScanned: (String) -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val previewView = remember { PreviewView(context) }
    // The analyser runs on its own thread and outlives a recomposition; reading the *current*
    // callback through this is what stops a stale lambda being invoked after the state moved on.
    val deliver by rememberUpdatedState(onScanned)
    var provider by remember { mutableStateOf<ProcessCameraProvider?>(null) }
    val executor = remember { Executors.newSingleThreadExecutor() }
    val reader = remember { QRCodeReader() }
    // Not a `mutableStateOf`: it is written from the analyser thread, and one code must produce one
    // delivery however many frames it appears in.
    val delivered = remember { AtomicBoolean(false) }

    Box(modifier = modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        AndroidView(factory = { previewView }, modifier = Modifier.fillMaxSize())

        // D2' §SCR-DA-027's "Anim: scan reticle" — the square the driver aims with.
        Box(
            modifier = Modifier
                .fillMaxWidth(RETICLE_FRACTION)
                .aspectRatio(1f)
                .border(
                    width = ControlTokens.BorderSelected,
                    color = ScannerColors.accent,
                    shape = RoundedCornerShape(MageRideTheme.radius.lg),
                ),
        )
    }

    LaunchedEffect(lifecycleOwner) {
        val cameraProvider = context.cameraProvider()
        val preview = Preview.Builder().build().apply { setSurfaceProvider(previewView.surfaceProvider) }
        val analysis = ImageAnalysis.Builder()
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .build()
            .apply {
                setAnalyzer(executor) { image ->
                    val payload = image.use { frame -> reader.decodeOrNull(frame) }
                    if (payload != null && delivered.compareAndSet(false, true)) {
                        deliver(payload)
                    }
                }
            }

        cameraProvider.unbindAll()
        cameraProvider.bindToLifecycle(lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
        provider = cameraProvider
    }

    DisposableEffect(Unit) {
        onDispose {
            provider?.unbindAll()
            executor.shutdown()
        }
    }
}

/**
 * One frame decoded, or `null` when it holds no readable QR code.
 *
 * **The luminance plane alone.** Plane 0 of a `YUV_420_888` frame *is* the greyscale image, which is
 * all a binarizer wants — so there is no colour conversion and no `Bitmap` allocated per frame, on
 * a screen that sees thirty of them a second. The source is built from `rowStride` and then cropped
 * to `width × height` because a stride may exceed the width by padding, and reading the padding as
 * pixels shears the image.
 *
 * `reset()` in a `finally` because `QRCodeReader` keeps decoder state between reads: a reused one
 * that has failed once starts refusing codes it would otherwise have found.
 */
private fun QRCodeReader.decodeOrNull(image: ImageProxy): String? {
    val plane = image.planes.firstOrNull() ?: return null
    val buffer = plane.buffer
    val luminance = ByteArray(buffer.remaining()).also(buffer::get)

    val source = PlanarYUVLuminanceSource(
        luminance,
        plane.rowStride,
        image.height,
        0,
        0,
        image.width,
        image.height,
        false,
    )

    return try {
        decode(BinaryBitmap(HybridBinarizer(source)), DECODE_HINTS).text
    } catch (_: NotFoundException) {
        null
    } catch (_: ChecksumException) {
        null
    } catch (_: FormatException) {
        null
    } finally {
        reset()
    }
}

/**
 * `TRY_HARDER` — a sticker on a tracker is small, often creased, and read one-handed at the
 * roadside. The extra work per frame is the difference between a scan and a retype.
 */
private val DECODE_HINTS = mapOf(DecodeHintType.TRY_HARDER to true)

/** How much of the width the aiming square takes. */
private const val RETICLE_FRACTION = 0.7f
