package lk.mageride.shared.data.api

import io.ktor.client.call.body
import io.ktor.client.request.HttpRequestBuilder
import io.ktor.client.request.forms.FormBuilder
import io.ktor.client.request.forms.MultiPartFormDataContent
import io.ktor.client.request.forms.formData
import io.ktor.client.request.parameter
import io.ktor.client.request.setBody
import io.ktor.client.statement.HttpResponse
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.Headers
import io.ktor.http.HttpHeaders
import io.ktor.http.contentType
import kotlinx.coroutines.CancellationException
import kotlinx.serialization.json.Json
import lk.mageride.shared.data.models.PageRequest

// Request bodies, query conventions and response decoding — the parts of a call that are about
// its payload rather than about its route. `ApiTransport.kt` owns the route and the headers.

/**
 * Sets a JSON request body.
 *
 * Reified on purpose: `setBody(x: Any)` loses the static type, and kotlinx.serialization cannot
 * find a serializer from a `KClass` on Kotlin/Native. Every call must therefore go through a
 * reified type parameter or the iOS build breaks at runtime, not at compile time.
 */
internal inline fun <reified T> HttpRequestBuilder.jsonBody(body: T) {
    contentType(ContentType.Application.Json)
    setBody(body)
}

/** Appends the `?cursor=&limit=` pair (`_shared.yaml#/components/parameters/{Cursor,Limit}`). */
internal fun HttpRequestBuilder.pageParameters(page: PageRequest) {
    parameter("cursor", page.cursor)
    parameter("limit", page.limit)
}

/**
 * One `multipart/form-data` file part.
 *
 * Bytes rather than a path: `commonMain` has no filesystem, and the four document-capture flows
 * (AL-43 drag-crop, proof-of-delivery photo, bank-transfer slip, GTFS zip) all hand the shared
 * layer an in-memory buffer anyway.
 *
 * Not a `data class` on purpose — a `ByteArray` compares by identity, and a generated `equals`
 * that quietly does the wrong thing is worse than none.
 *
 * @property fileName Name sent in the part's `Content-Disposition`.
 * @property bytes The file.
 * @property contentType Media type; the platform default suits an opaque upload.
 */
public class FileUpload(
    public val fileName: String,
    public val bytes: ByteArray,
    public val contentType: String = ContentType.Application.OctetStream.toString(),
)

/** Builds a `multipart/form-data` body. */
internal fun HttpRequestBuilder.multipartBody(build: FormBuilder.() -> Unit) {
    setBody(MultiPartFormDataContent(formData(block = build)))
}

/** Appends [upload] under the form field [name]. */
internal fun FormBuilder.filePart(name: String, upload: FileUpload) {
    append(
        key = name,
        value = upload.bytes,
        headers = Headers.build {
            append(HttpHeaders.ContentType, upload.contentType)
            append(HttpHeaders.ContentDisposition, "filename=\"${upload.fileName}\"")
        },
    )
}

/** Appends a text form field, skipping it when the value is absent. */
internal fun FormBuilder.textPart(name: String, value: String?) {
    if (value != null) append(name, value)
}

/**
 * Decodes a 2xx body, turning a schema mismatch into [MageRideError.Serialization].
 *
 * A response that does not match the contract is a platform bug, and it should reach the crash
 * reporter as one — not as whatever internal exception the JSON layer happened to raise.
 */
@Suppress("TooGenericExceptionCaught")
internal suspend inline fun <reified T> HttpResponse.decode(): T = try {
    body()
} catch (cause: Throwable) {
    throw cause.asDecodingFailure()
}

/**
 * Decodes a body the contract declares as `oneOf(Schema, null)`.
 *
 * Three reads are shaped that way — "the active session for this vehicle", "the active ride for
 * this passenger", "…for this driver" — and a literal `null` is the *normal* answer, not an error.
 * Content negotiation is bypassed for them because "no active ride" must not depend on how the
 * negotiation layer happens to treat a null body.
 */
internal suspend inline fun <reified T> HttpResponse.decodeOrNull(json: Json): T? {
    val text = readTextOrFail()
    if (text.isBlank() || text.trim() == NULL_LITERAL) return null
    return json.decodeOrFail(text)
}

@Suppress("TooGenericExceptionCaught")
internal suspend fun HttpResponse.readTextOrFail(): String = try {
    bodyAsText()
} catch (cause: Throwable) {
    throw cause.asDecodingFailure()
}

@Suppress("TooGenericExceptionCaught")
internal inline fun <reified T> Json.decodeOrFail(text: String): T = try {
    decodeFromString(text)
} catch (cause: Throwable) {
    throw cause.asDecodingFailure()
}

/**
 * Wraps a decoding failure, letting a cancellation and an already-typed error through untouched.
 *
 * Returning the throwable rather than throwing it keeps each caller to a single `throw`, and keeps
 * the decision readable where it is made.
 */
internal fun Throwable.asDecodingFailure(): Throwable = when (this) {
    is CancellationException -> this
    is MageRideError -> this
    else -> MageRideError.Serialization(this)
}

internal const val NULL_LITERAL: String = "null"
