package lk.mageride.shared.platform

import android.content.Context
import com.google.android.gms.tasks.Task
import com.google.android.play.core.integrity.IntegrityManagerFactory
import com.google.android.play.core.integrity.StandardIntegrityManager.PrepareIntegrityTokenRequest
import com.google.android.play.core.integrity.StandardIntegrityManager.StandardIntegrityTokenProvider
import com.google.android.play.core.integrity.StandardIntegrityManager.StandardIntegrityTokenRequest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.api.AttestationRequest
import java.security.MessageDigest
import java.util.Base64
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * The Android half of D-30: a **Play Integrity** token for the `X-Attestation` header.
 *
 * Uses the *standard* request flow, not the classic one. Classic requests need a server-issued
 * nonce and take seconds; standard requests are backed by a prepared token provider and answer in
 * tens of milliseconds, which is the difference between attestation being viable on twenty
 * operations and being viable on one. The gateway decodes whatever arrives through Google's
 * `decodeIntegrityToken` (`PlayIntegrityVerifier`, C008), which handles both.
 *
 * **The header is the token, unwrapped** — that is the format `AppAttestOptions` (C008) documents
 * for Android, and no spec states it. See this component's handoff.
 *
 * The `requestHash` binds the token to `"<METHOD> <path>"`. The gateway does not check it today;
 * it is sent anyway because it costs one hash and it is the only thing that would stop a token
 * captured on a harmless call being replayed onto a payment (a check C128 can then turn on
 * without an app release).
 *
 * @param context Any context; the application context is retained.
 * @param cloudProjectNumber The Google Cloud project number from the Play Console. Wrong or
 *   absent, every request answers `null` and the gateway rejects the call — which is the intended
 *   failure mode, not a silent bypass.
 */
public actual class PlatformAttestationProvider(context: Context, private val cloudProjectNumber: Long) :
    AttestationProvider {

    private val manager = IntegrityManagerFactory.createStandard(context.applicationContext)
    private val gate = Mutex()
    private var prepared: StandardIntegrityTokenProvider? = null

    /**
     * Prepares the token provider ahead of the first attested call.
     *
     * Optional but worth doing from the app shell at start-up: without it the first sensitive
     * mutation of the session pays the whole preparation cost, and the first sensitive mutation
     * is `POST /v1/auth/otp/request`.
     *
     * @return `true` when Play Integrity is usable on this device and build.
     */
    public suspend fun warmUp(): Boolean = tokenProvider() != null

    override suspend fun attestationToken(request: AttestationRequest): String? {
        val provider = tokenProvider() ?: return null
        return try {
            provider
                .request(
                    StandardIntegrityTokenRequest.builder()
                        .setRequestHash(requestHash(request.clientData))
                        .build(),
                )
                .await()
                .token()
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Exception) {
            // A prepared provider goes stale (Google rotates it, Play Services updates under us).
            // Drop it so the next call re-prepares rather than failing forever.
            gate.withLock { prepared = null }
            null
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun tokenProvider(): StandardIntegrityTokenProvider? = gate.withLock {
        prepared ?: try {
            manager
                .prepareIntegrityToken(
                    PrepareIntegrityTokenRequest.builder()
                        .setCloudProjectNumber(cloudProjectNumber)
                        .build(),
                )
                .await()
                .also { prepared = it }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Exception) {
            // No Play Services, no network on a cold start, a misconfigured project number. The
            // honest answer is "this build cannot attest", never an invented header.
            null
        }
    }

    private fun requestHash(clientData: String): String = Base64.getUrlEncoder().withoutPadding().encodeToString(
        MessageDigest.getInstance("SHA-256").digest(clientData.encodeToByteArray()),
    )
}

/** Bridges one Play Services [Task] to a coroutine, without pulling in `kotlinx-coroutines-play-services`. */
private suspend fun <T> Task<T>.await(): T = suspendCancellableCoroutine { continuation ->
    addOnCompleteListener { completed ->
        val failure = completed.exception
        when {
            failure != null -> continuation.resumeWithException(failure)
            completed.isCanceled -> continuation.cancel()
            else -> continuation.resume(completed.result)
        }
    }
}
