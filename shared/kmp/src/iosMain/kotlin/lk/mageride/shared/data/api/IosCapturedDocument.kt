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
    file = fileUploadOf(fileName = fileName, data = data, contentType = contentType),
    capturedVia = capturedVia,
)

/**
 * A bare [FileUpload] from iOS bytes — the same `memcpy` for a part that carries **no** provenance
 * (C093).
 *
 * [capturedDocument] is the AL-43 form and is the one to reach for whenever the file is a *document*:
 * `docs.uploads.captured_via` is what the Verification-Officer queue sorts on, and a document that
 * arrived without it is a scan nobody can trust. This one is for the two multipart parts on the
 * app-facing surface that are **not** documents and declare no `…CapturedVia` field beside them —
 * `POST /v1/support/screenshots` (US-16.2) and P-10's delivery proof — where stamping a provenance
 * would be inventing a column the contract does not have.
 *
 * The `contentType` default is spelled at the call site for the reason every other `iosMain` helper
 * here exists: a Kotlin default argument does not survive the Objective-C export, so a Swift caller
 * has to pass one anyway and it may as well be the honest one.
 *
 * @param fileName Name for the part's `Content-Disposition`.
 * @param data The bytes.
 * @param contentType Media type, e.g. `image/jpeg`.
 */
public fun fileUploadOf(fileName: String, data: NSData, contentType: String): FileUpload =
    FileUpload(fileName = fileName, bytes = data.toByteArray(), contentType = contentType)
