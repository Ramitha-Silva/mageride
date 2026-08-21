package lk.mageride.shared.data.api

import io.ktor.client.HttpClient
import io.ktor.client.engine.HttpClientEngine
import kotlinx.serialization.json.Json
import lk.mageride.shared.data.api.comms.NotificationApi
import lk.mageride.shared.data.api.comms.VoipApi
import lk.mageride.shared.data.api.content.ContentApi
import lk.mageride.shared.data.api.dispatch.DispatchApi
import lk.mageride.shared.data.api.fare.FareApi
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.query.AppLanguage
import lk.mageride.shared.data.api.query.LocalisedQueryApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.api.safety.SafetyApi
import lk.mageride.shared.data.api.subscription.SubscriptionApi
import lk.mageride.shared.data.api.support.SupportApi
import lk.mageride.shared.data.api.transit.TransitApi
import lk.mageride.shared.data.api.trip.TripStateApi
import lk.mageride.shared.data.api.version.VersionApi
import lk.mageride.shared.data.api.wallet.WalletApi
import org.koin.core.module.Module
import org.koin.dsl.module
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * The C013 slice of the Koin graph.
 *
 * **Two bindings the app must supply**, because `commonMain` cannot:
 * - [HttpClientEngine] — OkHttp on Android, Darwin on iOS, `MockEngine` in a test.
 * - [ApiConfig] — which gateway, which build, which platform.
 *
 * Everything else has a default here, so the graph resolves today and C014 can replace two of
 * them without touching any app:
 * - [TokenProvider] → [TokenProvider.Anonymous]
 * - [AttestationProvider] → [AttestationProvider.Unavailable]
 *
 * Koin's later definition wins, so C014's module simply declares the real ones and is appended
 * after this one in [lk.mageride.shared.di.sharedModules].
 */
@OptIn(ExperimentalTime::class)
public val apiModule: Module = module {
    single { MageRideApiSignals() }
    single<TokenProvider> { TokenProvider.Anonymous }
    single<AttestationProvider> { AttestationProvider.Unavailable }
    single<IdempotencyKeyGenerator> { UlidIdempotencyKeyGenerator() }

    // One breaker for the whole process: two clients that shared a transport but not a breaker
    // would each need their own five failures before either noticed a service was down.
    single { CircuitBreaker(get<ApiConfig>().circuitBreaker) { Clock.System.now().toEpochMilliseconds() } }

    single<HttpClient> {
        mageRideHttpClient(
            engine = get(),
            config = get(),
            tokens = get(),
            attestation = get(),
            signals = get(),
            breaker = get(),
            json = get(),
        )
    }

    single { ApiTransport(http = get(), config = get(), idempotencyKeys = get(), json = get<Json>()) }
    single { MageRideApi(transport = get(), signals = get()) }

    single<IamApi> { get<MageRideApi>().iam }
    single<RegistryApi> { get<MageRideApi>().registry }
    single<TripStateApi> { get<MageRideApi>().tripState }
    single<RideApi> { get<MageRideApi>().ride }
    single<DispatchApi> { get<MageRideApi>().dispatch }
    single<FareApi> { get<MageRideApi>().fare }
    single<SubscriptionApi> { get<MageRideApi>().subscription }
    single<WalletApi> { get<MageRideApi>().wallet }
    // The one client with a language on it (D-26). `getOrNull` rather than `get`, so an app that
    // has not bound an `AppLanguage` — both iOS apps today — is answered exactly as before.
    single<QueryApi> { LocalisedQueryApi(get<MageRideApi>().query, getOrNull<AppLanguage>()) }
    single<TransitApi> { get<MageRideApi>().transit }
    single<SafetyApi> { get<MageRideApi>().safety }
    single<SupportApi> { get<MageRideApi>().support }
    single<ContentApi> { get<MageRideApi>().content }
    single<VoipApi> { get<MageRideApi>().voip }
    single<NotificationApi> { get<MageRideApi>().notification }
    single<VersionApi> { get<MageRideApi>().version }
}
