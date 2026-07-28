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
 * @property response The response body's serializer, or `null` when the operation has no body.
 * @property request The request body's serializer, or `null` when the operation carries its input
 *   in the path or the query string. 85 of the 176 take a JSON body; the contract checks validate
 *   the outbound half against `requestBody` exactly as they validate the inbound half against
 *   `responses`, because a client can drift in either direction.
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
) {
    /** Whether the success response carries a body at all. */
    public val hasBody: Boolean get() = response != null

    override fun toString(): String = "$method $path ($operationId)"
}
