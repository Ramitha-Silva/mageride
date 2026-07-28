package lk.mageride.shared.testing.fake

import io.ktor.client.HttpClient
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.ApiLogLevel
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.api.CircuitBreaker
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.api.MageRideApi
import lk.mageride.shared.data.api.MageRideApiSignals
import lk.mageride.shared.data.api.TokenProvider
import lk.mageride.shared.data.api.mageRideHttpClient
import lk.mageride.shared.data.models.ClientPlatform
import kotlin.random.Random

/**
 * A real [MageRideApi] on a fake backend.
 *
 * The DoD asks for "a fake with the same surface" for every typed client, and this is the reading
 * that makes that literally true: the sixteen clients here are the **production** ones —
 * `KtorRideApi`, `KtorWalletApi`, all of them — talking to [FakeApiBackend] over MockEngine. Not a
 * second implementation that has to be kept in step, and not a `RideApi` stub that skips the parts
 * of a call that actually break: the idempotency key is still minted once and reused on a replay,
 * the `401` still refreshes exactly once, the `426` still raises the update wall, and every body
 * still goes through the same serializer an app will.
 *
 * ```kotlin
 * val backend = FakeApiBackend()
 * val api = backend.mageRideApi()
 * val ride = api.ride.getRide(Fixtures.RIDE_ID)     // synthesised, fully populated
 * ```
 *
 * @param config Gateway and build identity. The default points at a URL that does not resolve, on
 *   purpose — nothing here should ever reach a network.
 * @param tokens Session credential. Pass a [FakeTokenProvider] to drive the refresh path.
 * @param attestation D-30 supplier. The default cannot attest, as an emulator cannot.
 * @param signals Where a `426` is published as well as thrown.
 * @param idempotencyKeys Defaults to [SequentialIdempotencyKeys], so "the retry reused the key" is
 *   an equality assertion rather than an identity one.
 * @param random Seeded, so a backoff-jitter assertion is reproducible.
 */
@Suppress("LongParameterList")
public fun FakeApiBackend.mageRideApi(
    config: ApiConfig = TestApiConfig,
    tokens: TokenProvider = TokenProvider.Anonymous,
    attestation: AttestationProvider = AttestationProvider.Unavailable,
    signals: MageRideApiSignals = MageRideApiSignals(),
    idempotencyKeys: IdempotencyKeyGenerator = SequentialIdempotencyKeys(),
    breaker: CircuitBreaker = CircuitBreaker(config.circuitBreaker) { 0L },
    random: Random = Random(TEST_SEED),
): MageRideApi = MageRideApi(
    transport = ApiTransport(
        http = httpClient(config, tokens, attestation, signals, breaker, random),
        config = config,
        idempotencyKeys = idempotencyKeys,
    ),
    signals = signals,
)

@Suppress("LongParameterList")
private fun FakeApiBackend.httpClient(
    config: ApiConfig,
    tokens: TokenProvider,
    attestation: AttestationProvider,
    signals: MageRideApiSignals,
    breaker: CircuitBreaker,
    random: Random,
): HttpClient = mageRideHttpClient(
    engine = engine,
    config = config,
    tokens = tokens,
    attestation = attestation,
    signals = signals,
    breaker = breaker,
    random = random,
)

/**
 * The gateway identity every fake-backed client uses.
 *
 * A hostname that does not resolve, so a test that somehow escapes MockEngine fails at the socket
 * rather than reaching something real.
 */
public val TestApiConfig: ApiConfig = ApiConfig(
    baseUrl = "https://api.test.invalid",
    appVersion = "1.4.0",
    platform = ClientPlatform.ANDROID,
    logLevel = ApiLogLevel.NONE,
)

/**
 * Idempotency keys a test can predict.
 *
 * The whole point of R-14/R-18 is that a retry carries the **same** key as the attempt it repeats,
 * and that is only assertable if the keys are not random.
 */
public class SequentialIdempotencyKeys : IdempotencyKeyGenerator {

    private var minted = 0

    /** How many keys have been handed out. One per logical call, never one per attempt. */
    public val count: Int get() = minted

    override fun next(): String {
        minted++
        return PREFIX + minted.toString().padStart(SUFFIX_WIDTH, '0')
    }

    private companion object {
        const val PREFIX = "TESTIDEMPOTENCYKEY"
        const val SUFFIX_WIDTH = 8
    }
}

/** Seeded so a jitter assertion is reproducible across runs and across hosts. */
private const val TEST_SEED = 20260727
