package lk.mageride.shared.testing.fake

import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.api.AttestationRequest
import lk.mageride.shared.data.api.TokenProvider
import lk.mageride.shared.platform.SecureStore

/**
 * A [TokenProvider] that counts what the pipeline asked of it.
 *
 * The rules worth asserting are all *arithmetic on calls*: a `401` refreshes exactly once and
 * replays exactly once, a second `401` ends the session (D-29), and a caller whose token has
 * already been rotated past replays without rotating again — which is only observable through
 * [staleTokens].
 *
 * @param initialToken What [accessToken] answers before any refresh. `null` for no session.
 * @param rotatedToken What it answers after a successful refresh.
 * @param refreshSucceeds Whether [refresh] rotates or reports the session unrecoverable.
 */
public class FakeTokenProvider(
    initialToken: String? = "access-1",
    private val rotatedToken: String? = "access-2",
    private val refreshSucceeds: Boolean = true,
) : TokenProvider {

    private var token: String? = initialToken

    /** How many times the pipeline asked for a refresh. */
    public var refreshCalls: Int = 0
        private set

    /** How many times the pipeline gave up on the session. */
    public var authenticationLostCalls: Int = 0
        private set

    /**
     * The `staleAccessToken` values the pipeline reported, in order.
     *
     * What makes "collapse on the token that failed, not on a lock" assertable: a second caller
     * arriving after the rotation reports the *old* token and must be answered without a second
     * rotation, because the refresh token is single-use and racing it revokes the family (D-29).
     */
    public val staleTokens: MutableList<String?> = mutableListOf()

    override suspend fun accessToken(): String? = token

    override suspend fun refresh(staleAccessToken: String?): Boolean {
        refreshCalls++
        staleTokens += staleAccessToken
        if (!refreshSucceeds) return false
        token = rotatedToken
        return true
    }

    override suspend fun onAuthenticationLost() {
        authenticationLostCalls++
    }
}

/**
 * An [AttestationProvider] that vouches for everything and remembers what it was asked about.
 *
 * The gateway binds an App Attest assertion to `SHA-256("<METHOD> <path>")` (C008), so the pair a
 * provider is handed is part of the contract — [requests] is what lets a test assert that the
 * twenty D-30 operations, and only those, reached here.
 *
 * @param token The header value to supply, or `null` to model a build that cannot attest.
 */
public class RecordingAttestationProvider(private val token: String? = "attestation-token") : AttestationProvider {

    /** Every attestation the pipeline asked for, in order. */
    public val requests: MutableList<AttestationRequest> = mutableListOf()

    /** The operation ids that were attested, in order. */
    public val operationIds: List<String> get() = requests.map { it.operationId }

    override suspend fun attestationToken(request: AttestationRequest): String? {
        requests += request
        return token
    }
}

/**
 * A [SecureStore] in a map.
 *
 * Stands in for the Android Keystore and the iOS Keychain, neither of which exists in a host test.
 * [clears] is counted because logout, revocation and PDPA erasure are all "did the store actually
 * get emptied" assertions, and an empty map does not distinguish "cleared" from "never written".
 */
public class InMemorySecureStore : SecureStore {

    /** Everything currently stored, readable so a test can assert what a token looks like at rest. */
    public val values: MutableMap<String, String> = mutableMapOf()

    /** How many times the namespace was wiped. */
    public var clears: Int = 0
        private set

    override suspend fun read(key: String): String? = values[key]

    override suspend fun write(key: String, value: String) {
        values[key] = value
    }

    override suspend fun delete(key: String) {
        values.remove(key)
    }

    override suspend fun clear() {
        clears++
        values.clear()
    }
}
