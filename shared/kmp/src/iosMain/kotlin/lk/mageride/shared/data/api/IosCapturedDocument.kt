package lk.mageride.shared.data.api

import lk.mageride.shared.data.models.registry.CaptureSource
import lk.mageride.shared.mqtt.toByteArray
import platform.Foundation.NSData

/**
 * Builds a [CapturedDocument] from the bytes an iOS picker or SCR-DI-005 produced.
 *
 * **The conversion is here because it cannot be written efficiently in Swift.** `FileUpload` takes a
 * `ByteArray`, and Kotlin/Native exports that as `KotlinByteArray` — a class whose only Swift-facing
 * mutator is `set(index:value:)`, one Objective-C message per byte. A three-megabyte licence
 * photograph is three million messages. Kotlin has `memcpy` (see `NSData.toByteArray`), so the copy
 * costs a single `memcpy` and Swift passes the `NSData` it already holds.
 *
 * Same argument the app's own conventions make about `IosAppConfig` and `IosMqttPlan`: the bridge
 * carries values, and whichever side can express the operation honestly owns it.
 *
 * @param fileName Name for the part's `Content-Disposition`.
 * @param data The image bytes, already perspective-corrected when they came from the scanner.
 * @param contentType Media type, e.g. `image/jpeg`.
 * @param capturedVia How the image was obtained (AL-43) — never defaulted, because a provenance the
 *   client guessed is a provenance the Verification-Officer queue cannot trust.
 */
public fun capturedDocument(
    fileName: String,
    data: NSData,
    contentType: String,
    capturedVia: CaptureSource,
): CapturedDocument = CapturedDocument(
    file = FileUpload(fileName = fileName, bytes = data.toByteArray(), contentType = contentType),
    capturedVia = capturedVia,
)
