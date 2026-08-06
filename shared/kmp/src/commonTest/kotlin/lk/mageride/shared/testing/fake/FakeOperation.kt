package lk.mageride.shared.testing.fake

import kotlinx.serialization.KSerializer
import lk.mageride.shared.data.api.ApiService

/**
 * One row of the operation table: a route, its success status, and the DTO its body decodes into.
 *
 * @property operationId The contract's own `operationId`. Every request carries it in its
 *   attributes (C013's `ApiTransport`), which is what [FakeApiBackend] routes on — no path
 *   matching, so a fake response can never be attached to the wrong operation by a typo in a URL.
 * @property service The owning service, as `backend/contracts/{service}.id.yaml`.
 * @property method The HTTP verb, uppercased.
 * @property path The templated path, `{parameter}` segments included, exactly as the YAML spells
 *   it. Not used for routing; it is here so a failure message names a route a human recognises and
 *   so `ApiOperationTableTest` can compare the table against the contract.
 * @property status The success status the contract declares — `202` for a booking, `204` for the
 *   eleven bodiless mutations, `302` for the GTFS download.
 * @property response The response body's **JSON** serializer, or `null` when the operation answers
 *   with no body at all or with [binary] bytes.
 * @property request The request body's serializer, or `null` when the operation carries its input
 *   in the path or the query string. 85 of the 176 take a JSON body; the contract checks validate
 *   the outbound half against `requestBody` exactly as they validate the inbound half against
 *   `responses`, because a client can drift in either direction.
 * @property binary Whether the success response is bytes rather than JSON — `image/jpeg`,
 *   `application/pdf`. See the note on [hasBody] (Δ C076a).
 */
@Suppress("LongParameterList")
public class FakeOperation internal constructor(
    public val operationId: String,
    public val service: ApiService,
    public val method: String,
    public val path: String,
    public val status: Int,
    public val response: KSerializer<*>?,
    public val request: KSerializer<*>? = null,
    public val binary: Boolean = false,
) {
    /**
     * Whether the success response carries a **JSON** body.
     *
     * **False for a binary operation, and that is deliberate rather than a shortcut.** The three
     * reads that answer with bytes — `getModeBFile` (AL-49's signed document link),
     * `getSupportScreenshot` and `downloadSignedGtfsObject` — declare `image/jpeg`, `image/png` or
     * `application/pdf` and no `application/json` schema at all. `ApiOperationTableTest` compares
     * this flag against exactly that: `responseSchema(status)` reads the JSON media type, so a
     * binary operation has no schema to check a body against and none to synthesise one from.
     *
     * Use [binary] to tell "answers with bytes" apart from "answers with nothing" — the `204`
     * mutations and the `302` GTFS redirect. Before C076a the table could express only the latter
     * two, which is why those three rows were missing entirely (MCS-03's recorded prerequisite).
     */
    public val hasBody: Boolean get() = response != null

    override fun toString(): String = "$method $path ($operationId)"
}
