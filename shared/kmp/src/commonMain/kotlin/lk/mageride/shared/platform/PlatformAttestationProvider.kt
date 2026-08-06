package lk.mageride.shared.platform

import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.api.AttestationRequest

/**
 * The platform's D-30 attestation supplier: **Play Integrity** on Android, **App Attest** on iOS.
 *
 * C013 owns *when* the header is asked for — the twenty operations declaring `X-Attestation` in
 * the contracts under `backend/contracts` — and this owns *what* the header contains. The wire
 * format is the one `backend/src/ApiGateway/Attestation` defines and no spec does (see the C008
 * handoff and this component's notes):
 *
 * | Platform | `X-Attestation` |
 * |---|---|
 * | Android | the Play Integrity token, unwrapped |
 * | iOS | `base64url(keyId) "." base64url(assertion)` |
 *
 * As with [PlatformSecureStore] the constructors differ — Android needs a `Context` and the Play
 * Console cloud project number, iOS needs somewhere to keep its App Attest key id — so the app
 * builds it and `commonMain` sees only [AttestationProvider].
 *
 * **Fail soft, never fake.** Every actual returns `null` when it cannot produce a genuine verdict
 * (Play Services missing, App Attest unsupported, the device not yet registered). The request then
 * goes out without the header and the gateway answers `401 attestation-failed`, which is the
 * honest outcome — a client that invented a value would turn a hard control into a guess.
 *
 * [attestationToken] is re-declared rather than only inherited, for the reason
 * [PlatformSecureStore]'s KDoc gives (Δ C085).
 */
public expect class PlatformAttestationProvider : AttestationProvider {

    override suspend fun attestationToken(request: AttestationRequest): String?
}
