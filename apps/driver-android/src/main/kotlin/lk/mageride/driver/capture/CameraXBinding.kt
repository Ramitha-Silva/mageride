package lk.mageride.driver.capture

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageCapture
import androidx.camera.core.ImageCaptureException
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import kotlinx.coroutines.suspendCancellableCoroutine
import java.util.concurrent.Executor
import kotlin.coroutines.resume

// The CameraX half of SCR-DA-005 — the viewfinder, the shutter, and nothing about cropping.
//
// Split from `DocumentScannerScreen` deliberately: what is here is PLUMBING (bind a use case to a
// lifecycle, turn a `ListenableFuture` and a callback into suspend calls, read a permission), and
// what is there is the wireframe. ADD AL-43 names `ImageCapture` for Android specifically, so the
// choice of API is the contract rather than a preference.

/**
 * 4:3 on both use cases, and the same selector object for each.
 *
 * The preview and the still have to be the same rectangle or the crop quad means two different
 * things — see `DocumentScannerScreen`'s KDoc. Fixing the ratio rather than taking the sensor's is
 * what makes that true on a handset whose capture and preview streams have different native
 * aspects.
 */
internal val FOUR_BY_THREE: ResolutionSelector = ResolutionSelector.Builder()
    .setAspectRatioStrategy(AspectRatioStrategy.RATIO_4_3_FALLBACK_AUTO_STRATEGY)
    .build()

/**
 * CameraX `Preview` + `ImageCapture`, bound to this composable's lifecycle.
 *
 * The provider is unbound in `onDispose`: leaving the camera bound is what makes the second visit
 * to the scanner in one session open on a black frame.
 */
@Composable
internal fun CameraPreview(torchOn: Boolean, imageCapture: ImageCapture, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val previewView = remember {
        PreviewView(context).apply {
            // FIT_CENTER, not the default FILL_CENTER — see the screen's KDoc on why the preview
            // and the capture have to be the same rectangle.
            scaleType = PreviewView.ScaleType.FIT_CENTER
        }
    }
    var camera by remember { mutableStateOf<Camera?>(null) }
    var provider by remember { mutableStateOf<ProcessCameraProvider?>(null) }

    AndroidView(factory = { previewView }, modifier = modifier.fillMaxSize())

    LaunchedEffect(lifecycleOwner) {
        val cameraProvider = context.cameraProvider()
        val preview = Preview.Builder()
            .setResolutionSelector(FOUR_BY_THREE)
            .build()
            .apply { setSurfaceProvider(previewView.surfaceProvider) }

        cameraProvider.unbindAll()
        camera = cameraProvider.bindToLifecycle(
            lifecycleOwner,
            CameraSelector.DEFAULT_BACK_CAMERA,
            preview,
            imageCapture,
        )
        provider = cameraProvider
    }

    LaunchedEffect(torchOn, camera) {
        camera?.cameraControl?.enableTorch(torchOn)
    }

    DisposableEffect(Unit) {
        onDispose { provider?.unbindAll() }
    }
}

/**
 * `MAXIMIZE_QUALITY`, not the default minimum latency: latency costs exactly the sharpness the
 * drag-crop exists to preserve (BR-28.4), and a document scan is not a burst.
 */
internal fun newImageCapture(): ImageCapture = ImageCapture.Builder()
    .setResolutionSelector(FOUR_BY_THREE)
    .setCaptureMode(ImageCapture.CAPTURE_MODE_MAXIMIZE_QUALITY)
    .build()

/** Takes the picture and hands the JPEG, with its sensor rotation, to the view model. */
internal suspend fun ImageCapture.deliverTo(viewModel: DocumentScannerViewModel) {
    val proxy = takePicture()
    if (proxy == null) {
        viewModel.onCaptureFailed()
        return
    }
    proxy.use { image ->
        val buffer = image.planes[0].buffer
        val bytes = ByteArray(buffer.remaining()).also(buffer::get)
        viewModel.onCaptured(bytes, image.imageInfo.rotationDegrees)
    }
}

/** `takePicture` as a suspend call. `null` is a camera that could not take the picture. */
private suspend fun ImageCapture.takePicture(): ImageProxy? = suspendCancellableCoroutine { continuation ->
    takePicture(
        DIRECT_EXECUTOR,
        object : ImageCapture.OnImageCapturedCallback() {
            override fun onCaptureSuccess(image: ImageProxy) {
                continuation.resume(image)
            }

            override fun onError(exception: ImageCaptureException) {
                continuation.resume(null)
            }
        },
    )
}

/** `ProcessCameraProvider.getInstance` is a `ListenableFuture`; this is it as a suspend call. */
private suspend fun Context.cameraProvider(): ProcessCameraProvider {
    val future = ProcessCameraProvider.getInstance(this)
    return suspendCancellableCoroutine { continuation ->
        future.addListener({ continuation.resume(future.get()) }, ContextCompat.getMainExecutor(this))
    }
}

/** Whether the camera grant is held. A denial is a state SCR-DA-005 draws, never a crash. */
internal fun Context.hasCameraPermission(): Boolean =
    ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

/**
 * The capture callback runs on the caller's thread.
 *
 * Nothing in it does work — it resumes a coroutine, and the decode that follows is already on
 * `Dispatchers.Default` inside `DocumentImaging`. A pool per shot would be a thread per photograph.
 */
private val DIRECT_EXECUTOR = Executor { command -> command.run() }
