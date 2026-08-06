package lk.mageride.shared.platform

import kotlinx.cinterop.BetaInteropApi
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.UByteVar
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.allocArray
import kotlinx.cinterop.convert
import kotlinx.cinterop.memScoped
import kotlinx.cinterop.usePinned
import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.api.AttestationRequest
import platform.CoreCrypto.CC_SHA256
import platform.CoreCrypto.CC_SHA256_DIGEST_LENGTH
import platform.DeviceCheck.DCAppAttestService
import platform.Foundation.NSData
import platform.Foundation.NSError
import platform.Foundation.base64EncodedStringWithOptions
import platform.Foundation.create
import kotlin.coroutines.resume

/**
 * One completed App Attest registration, ready to be sent to `iam-svc`.
 *
 * **Spec gap.** `backend/contracts/iam.yaml` has no route that accepts this, and the gateway's
 * `IAttestedKeyStore` (C008) is fed from `iam.devices.attestation_verified_at` — a column with no
 * endpoint writing it. Until D3'/`iam.yaml` gain an App Attest registration route (and a
 * challenge to bind it to), an iOS build can produce assertions but nothing on the server has the
 * public key to check them against, so the edge answers `app-attest-unknown-key`. Recorded in the
 * C014 handoff.
 *
 * @property keyId The Secure Enclave key id, base64url of its raw bytes — the same spelling the
 *   `X-Attestation` header's first segment carries, so the server can key its store on it.
 * @property attestationObject Apple's CBOR attestation object, base64url.
 */
public data class AppAttestRegistration(val keyId: String, val attestationObject: String)

/**
 * The iOS half of D-30: **App Attest** assertions for the `X-Attestation` header.
 *
 * The wire format is the one `AppAttestVerifier` (C008) parses and no spec states:
 * `base64url(keyId) "." base64url(assertion)`, where the assertion is signed over
 * `SHA-256("<METHOD> <path>")` — which is why [AttestationRequest] carries the method and path at
 * all. The assertion's signature counter is what makes a captured header worthless a moment later,
 * so a fresh assertion is generated per call rather than cached.
 *
 * The key itself is generated in the **Secure Enclave** by `DCAppAttestService` and never leaves
 * it; only its id is stored, in [SecureStore], so it survives a logout — regenerating a key would
 * mean re-registering with the server and would look exactly like a cloned device.
 *
 * @param keyStore Where the key id lives. Pass the same [PlatformSecureStore] the session uses.
 * @param keyIdEntry Store key for the id. Deliberately outside
 *   [lk.mageride.shared.domain.auth.AuthConfig.storeKey]'s prefix so a logout does not take it.
 */
public actual class PlatformAttestationProvider(
    private val keyStore: SecureStore,
    private val keyIdEntry: String = DEFAULT_KEY_ID_ENTRY,
) : AttestationProvider {

    private val service = DCAppAttestService.sharedService
    private val gate = Mutex()

    actual override suspend fun attestationToken(request: AttestationRequest): String? = keyId()?.let { keyId ->
        sha256(request.clientData)
            ?.let { hash -> assertion(keyId, hash) }
            ?.let { signed -> rawKeyId(keyId) + "." + signed.base64Url() }
    }

    /**
     * Generates the attestation object for the current key, for a server-issued [challenge].
     *
     * Call once per install, from the app shell, and send the result to `iam-svc` — see
     * [AppAttestRegistration] for why that endpoint does not exist yet.
     */
    public suspend fun prepareRegistration(challenge: ByteArray): AppAttestRegistration? = keyId()?.let { keyId ->
        sha256Of(challenge)
            ?.let { hash -> attest(keyId, hash) }
            ?.let { attested -> AppAttestRegistration(rawKeyId(keyId), attested.base64Url()) }
    }

    /**
     * The stored Secure Enclave key id, generating one on first use.
     *
     * `null` on a device or simulator where App Attest is unsupported — an assertion from a key
     * that cannot exist is not something to fake.
     */
    private suspend fun keyId(): String? = gate.withLock {
        if (!service.isSupported()) {
            null
        } else {
            keyStore.read(keyIdEntry) ?: generateKey()?.also { keyStore.write(keyIdEntry, it) }
        }
    }

    private suspend fun assertion(keyId: String, clientDataHash: NSData): NSData? =
        suspendCancellableCoroutine { continuation ->
            service.generateAssertion(keyId, clientDataHash) { data: NSData?, error: NSError? ->
                continuation.resumeWithPayload(data, error)
            }
        }

    private suspend fun attest(keyId: String, clientDataHash: NSData): NSData? =
        suspendCancellableCoroutine { continuation ->
            service.attestKey(keyId, clientDataHash) { data: NSData?, error: NSError? ->
                continuation.resumeWithPayload(data, error)
            }
        }

    private suspend fun generateKey(): String? = suspendCancellableCoroutine { continuation ->
        service.generateKeyWithCompletionHandler { keyId: String?, error: NSError? ->
            continuation.resume(if (error != null) null else keyId)
        }
    }

    private companion object {
        const val DEFAULT_KEY_ID_ENTRY = "lk.mageride.attest.key-id"
    }
}

/** Resumes with the payload, or with `null` when the framework reported an error. */
private fun CancellableContinuation<NSData?>.resumeWithPayload(data: NSData?, error: NSError?) {
    resume(if (error != null) null else data)
}

/**
 * Apple hands back a standard-base64 key id; the gateway keys its store on base64url characters
 * only (`AppAttestVerifier.TrySplit`), so it is re-spelled once, here, and the same spelling goes
 * into [AppAttestRegistration].
 */
private fun rawKeyId(keyId: String): String = keyId.trimEnd('=').replace('+', '-').replace('/', '_')

@OptIn(ExperimentalForeignApi::class)
private fun NSData.base64Url(): String =
    base64EncodedStringWithOptions(0u).trimEnd('=').replace('+', '-').replace('/', '_')

private fun sha256(input: String): NSData? = sha256Of(input.encodeToByteArray())

@OptIn(ExperimentalForeignApi::class, BetaInteropApi::class)
private fun sha256Of(input: ByteArray): NSData? {
    if (input.isEmpty()) return null
    return memScoped {
        val digest = allocArray<UByteVar>(CC_SHA256_DIGEST_LENGTH)
        input.usePinned { pinned ->
            CC_SHA256(pinned.addressOf(0), input.size.convert(), digest)
        }
        NSData.create(bytes = digest, length = CC_SHA256_DIGEST_LENGTH.convert())
    }
}
