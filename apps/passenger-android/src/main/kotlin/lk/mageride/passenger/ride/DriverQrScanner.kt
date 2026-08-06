package lk.mageride.passenger.ride

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.google.zxing.BinaryBitmap
import com.google.zxing.ChecksumException
import com.google.zxing.FormatException
import com.google.zxing.NotFoundException
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import lk.mageride.passenger.R
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * SCR-PA-017's *"📷 Scan driver's QR"* — a viewfinder that answers with the decoded payload.
 *
 * **A `Dialog`, not a destination.** The screen that asked for the scan stays composed underneath,
 * so the amount due and the chosen rail survive; a route would have to carry both and hand the
 * result back through the back stack. The driver app's SCR-DA-027 scanner made the same call.
 *
 * **`zxing:core` and CameraX, not an embedded scanner Activity.** The `-android-embedded` artifacts
 * ship their own Activity and theme; `zxing:core` is pure Java with no camera of its own, which is
 * why the analyser below does the frame handling. Same dependency pair the driver app uses.
 *
 * **The payload is not interpreted here.** It is the driver's own bank merchant string and goes to
 * `POST /v1/fare/pay/scan-driver-qr` exactly as read — decoding it further would be this app
 * making claims about somebody else's bank.
 */
@Composable
internal fun DriverQrScanner(onScanned: (String) -> Unit, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val currentOnScanned by rememberUpdatedState(onScanned)

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(
                text = stringResource(R.string.pay_scan),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.onSurface,
                textAlign = TextAlign.Center,
            )

            if (!context.hasCameraPermission()) {
                // Not a request: SCR-PA-005 owns permission rationale, and a dialog that asked
                // here would be a second permission UI the passenger has never seen before.
                Text(
                    text = stringResource(R.string.pay_scan_no_camera),
                    modifier = Modifier.padding(MageRideTheme.spacing.md),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                )
            } else {
                Box(
                    modifier = Modifier
                        .padding(vertical = MageRideTheme.spacing.md)
                        .size(ControlTokens.Viewfinder),
                ) {
                    CameraPreview(
                        modifier = Modifier.fillMaxSize(),
                        onDecoded = { payload ->
                            currentOnScanned(payload)
                        },
                    )
                }
            }

            TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.action_cancel))
            }
        }
    }
}

/**
 * The camera, bound to this composition.
 *
 * The analyser runs on its own single-threaded executor and is **latched**: a QR sits in frame for
 * dozens of frames, and reporting each one would post the same payment a dozen times.
 */
@Composable
private fun CameraPreview(modifier: Modifier, onDecoded: (String) -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val currentOnDecoded by rememberUpdatedState(onDecoded)

    val previewView = androidx.compose.runtime.remember { PreviewView(context) }

    DisposableEffect(previewView) {
        val executor = Executors.newSingleThreadExecutor()
        val latched = AtomicBoolean(false)
        val reader = QRCodeReader()
        val provider = ProcessCameraProvider.getInstance(context).get()

        val preview = Preview.Builder().build().also { it.surfaceProvider = previewView.surfaceProvider }
        val analysis = ImageAnalysis.Builder()
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .build()
            .also { useCase ->
                useCase.setAnalyzer(executor) { image ->
                    val payload = image.decodeQr(reader)
                    image.close()
                    if (payload != null && latched.compareAndSet(false, true)) {
                        previewView.post { currentOnDecoded(payload) }
                    }
                }
            }

        runCatching {
            provider.unbindAll()
            provider.bindToLifecycle(lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
        }

        onDispose {
            provider.unbindAll()
            executor.shutdown()
        }
    }

    AndroidView(factory = { previewView }, modifier = modifier)
}

/**
 * One frame, decoded — or `null`.
 *
 * The Y plane alone: a QR is black and white, so the chroma planes carry nothing the reader wants
 * and copying them would be two thirds of the work for none of the signal.
 */
private fun ImageProxy.decodeQr(reader: QRCodeReader): String? {
    val buffer = planes.firstOrNull()?.buffer ?: return null
    val bytes = ByteArray(buffer.remaining()).also(buffer::get)
    val source = PlanarYUVLuminanceSource(bytes, width, height, 0, 0, width, height, false)

    return try {
        reader.decode(BinaryBitmap(HybridBinarizer(source))).text
    } catch (_: NotFoundException) {
        null
    } catch (_: ChecksumException) {
        null
    } catch (_: FormatException) {
        null
    } finally {
        reader.reset()
    }
}

/** Whether the camera has been granted. SCR-PA-005 owns asking; this only reads. */
private fun Context.hasCameraPermission(): Boolean =
    ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
