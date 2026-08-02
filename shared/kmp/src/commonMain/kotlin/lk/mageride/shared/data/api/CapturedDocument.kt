package lk.mageride.shared.data.api

import io.ktor.client.request.forms.FormBuilder
import lk.mageride.shared.data.models.registry.CaptureSource

/**
 * One onboarding image and how it was captured (AL-43).
 *
 * **Δ MCS-01.** A pair rather than two parameters, because the two travel together on the wire —
 * `registry.yaml` puts a `…CapturedVia` beside every binary part — and because separating them
 * makes it possible to send four files and three provenances. The type is what stops that.
 *
 * @property file The bytes, already perspective-corrected when they came from the scanner.
 * @property capturedVia Which of the two ways this one was captured. Never defaulted; see
 *   [lk.mageride.shared.data.models.registry.CaptureSource].
 */
public class CapturedDocument(public val file: FileUpload, public val capturedVia: CaptureSource)

/** Appends [document] as a file part plus the `…CapturedVia` field the contract pairs with it. */
internal fun FormBuilder.capturedDocumentPart(name: String, document: CapturedDocument) {
    filePart(name, document.file)
    append("${name}CapturedVia", document.capturedVia.wire)
}
