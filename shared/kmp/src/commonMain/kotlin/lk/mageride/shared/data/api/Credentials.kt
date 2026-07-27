package lk.mageride.shared.data.api

/**
 * Where the request pipeline gets its `Authorization: Bearer <JWT>` from, and how it recovers
 * from a `401` (D3' §0 "Auth", D-29).
 *
 * **C013 owns the mechanism; C014 owns the state.** This module knows only that a token can be
 * fetched, that a `401` justifies exactly one refresh, and that a failed refresh means the
 * session is over. Where the tokens live (Android Keystore / iOS Keychain), when the refresh
 * token rotates, and what "single active device per app" (AL-08) does to the session belong to
 * `domain/auth` — C014 supplies the implementation and Koin swaps it in for [Anonymous].
 *
 * Implementations must be safe to call from several coroutines at once: the pipeline calls
 * [accessToken] on every attempt of every in-flight request.
 */
public interface TokenProvider {

    /** The current access token, or `null` when there is no session (public routes still work). */
    public suspend fun accessToken(): String?

    /**
     * Exchanges the rotating refresh token for a new access token.
     *
     * Called at most **once per request** — a second `401` after a successful refresh is taken
     * as "this credential is not the problem" and surfaces to the caller. Concurrent callers
     * must collapse onto one refresh; the opaque refresh token is single-use, and racing it
     * revokes the whole session family (D-29).
     *
     * @return `true` when a new access token is available and the request should be replayed.
     */
    public suspend fun refresh(): Boolean

    /** The session is unrecoverable: the refresh failed, or a replay still came back `401`. */
    public suspend fun onAuthenticationLost()

    public companion object {
        /**
         * No session at all: sends no `Authorization` header and never refreshes.
         *
         * The default binding, so the graph resolves before C014 lands and so the public
         * routes (`/v1/config/cities`, `/v1/version/check`, `/v1/trip-share/public/{token}`,
         * the OTP pair) are reachable from an unauthenticated app.
         */
        public val Anonymous: TokenProvider = object : TokenProvider {
            override suspend fun accessToken(): String? = null

            override suspend fun refresh(): Boolean = false

            override suspend fun onAuthenticationLost() = Unit
        }
    }
}

/**
 * Supplies the `X-Attestation` header the gateway validates on sensitive mutations (D-30).
 *
 * Play Integrity on Android, App Attest on iOS — neither can be produced from `commonMain`, so
 * C014 lands the `expect`/`actual` pair and binds it here. Until then [Unavailable] answers
 * `null` and the twenty attested operations fail at the edge with `401 attestation-failed`,
 * which is the honest outcome: a client that cannot attest must not pretend it did.
 */
public fun interface AttestationProvider {

    /**
     * A fresh attestation verdict for one call.
     *
     * @param operationId The contract's `operationId`, so a provider can bind the verdict to
     *   the operation or cache per operation class.
     * @return The header value, or `null` when this build cannot attest.
     */
    public suspend fun attestationToken(operationId: String): String?

    public companion object {
        /** The default binding: this platform cannot attest. */
        public val Unavailable: AttestationProvider = AttestationProvider { null }
    }
}
