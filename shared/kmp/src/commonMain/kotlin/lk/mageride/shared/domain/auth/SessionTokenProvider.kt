package lk.mageride.shared.domain.auth

import lk.mageride.shared.data.api.TokenProvider

/**
 * The real [TokenProvider], replacing C013's [TokenProvider.Anonymous] placeholder.
 *
 * Three lines of delegation, and that is the point: C013 owns *when* the pipeline asks for a
 * credential (attach on every attempt; on a `401`, refresh once and replay once; a second `401` is
 * [TokenProvider.onAuthenticationLost]), and [AuthSessionManager] owns *what* the answers mean.
 * Splitting it the other way — a provider that knew about `401`s — would put the D-29 rules in the
 * transport, where the MQTT token and the OTP flow could not reach them.
 *
 * @property sessions The session state machine this provider reads.
 */
public class SessionTokenProvider(private val sessions: AuthSessionManager) : TokenProvider {

    override suspend fun accessToken(): String? = sessions.accessToken()

    override suspend fun refresh(staleAccessToken: String?): Boolean = sessions.refresh(staleAccessToken)

    override suspend fun onAuthenticationLost(): Unit = sessions.onAuthenticationLost()
}
