package lk.mageride.shared.platform

import lk.mageride.shared.data.api.AttestationRequest

/**
 * The JVM cannot attest, and says so.
 *
 * D-30's verdict comes from Play Integrity or App Attest, both of which are statements about a
 * *device* and an installed, store-signed build. A server-side JVM is neither, so there is no
 * honest token to produce.
 *
 * **Fail soft, never fake** — the rule the common [PlatformAttestationProvider] documentation
 * states. Returning `null` sends the request without the header and the gateway answers
 * `401 attestation-failed` on the twenty operations that declare `X-Attestation`. Inventing a
 * value would turn a hard control into a guess; a harness that needs those operations turns
 * attestation off at the gateway instead, which is a deployment decision someone can see.
 */
public actual class PlatformAttestationProvider : lk.mageride.shared.data.api.AttestationProvider {

    actual override suspend fun attestationToken(request: AttestationRequest): String? = null
}
