package lk.mageride.shared.di

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.darwin.Darwin
import kotlinx.coroutines.flow.filterNotNull
import lk.mageride.shared.data.api.ApiConfig
import lk.mageride.shared.data.api.ApiLogLevel
import lk.mageride.shared.data.api.AttestationProvider
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.api.MageRideApi
import lk.mageride.shared.data.api.MageRideApiSignals
import lk.mageride.shared.data.api.UpgradeRequiredSignal
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.db.DatabaseDriverFactory
import lk.mageride.shared.db.MageRideApp
import lk.mageride.shared.db.MageRideDatabaseFactory
import lk.mageride.shared.db.PlatformDatabaseDriverFactory
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.MqttSessionToken
import lk.mageride.shared.domain.auth.MqttSessionTokenManager
import lk.mageride.shared.domain.auth.SessionEvent
import lk.mageride.shared.domain.dispatch.OfferSession
import lk.mageride.shared.domain.dispatch.OfferSessionState
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.mqtt.MqttConfig
import lk.mageride.shared.platform.PlatformAttestationProvider
import lk.mageride.shared.platform.PlatformSecureStore
import lk.mageride.shared.platform.SecureStore
import org.koin.core.Koin
import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * Everything an iOS app has to hand `:shared`, as **primitives**.
 *
 * The Android apps express this as a Koin module of their own (`driverAppModule`,
 * `passengerAppModule`) because they can write Kotlin. **Swift cannot**: `Module.single`,
 * `Koin.get` and `module { }` are all `inline` + `reified`, and an inline reified function is not
 * exported to Objective-C at all — so there is no way to build a Koin module or resolve a
 * definition from Swift. [Koin.kt]'s own KDoc anticipates this ("Swift cannot express Koin's
 * trailing-lambda DSL comfortably, so the shared layer owns the start-up call and iOS passes only
 * its extra modules"); [startIosGraph] is the missing half.
 *
 * **Primitives, not [ApiConfig] / [AuthConfig] / [MqttConfig].** Those three carry `kotlin.time
 * .Duration` fields, and `Duration` is an inline value class — Kotlin/Native exports it as its raw
 * `Long`, and a Swift call site would be passing unlabelled nanosecond counts into a constructor
 * whose defaults it cannot see (Kotlin default arguments do not survive the Objective-C export
 * either; every parameter becomes required). Taking primitives here means the **defaults stay
 * where they are documented** — they *are* the spec: a persistent MQTT session, a 60 s keepalive,
 * a 2-minute refresh skew.
 *
 * [ClientPlatform.IOS] is fixed here rather than passed for the same reason: an iOS build that sent
 * `X-Platform: android` would pass every gateway check and be invisible in D-31's telemetry.
 *
 * Not driver-specific and must not become so: C085 passes [AppSurface.DRIVER] / [MageRideApp.DRIVER],
 * C094 the passenger pair.
 *
 * @property baseUrl Gateway origin. The app never builds a URL from anything else.
 * @property appVersion `CFBundleShortVersionString`, sent as `X-App-Version` — D-31's input.
 * @property userAgent The `User-Agent` this build sends.
 * @property debug Debug build. Decides the log level, and nothing else.
 * @property mqttHost EMQX host for the position plane (D6' §3).
 * @property mqttPort 1883 plain in debug, 8883 TLS in release.
 * @property mqttTls Never false outside a local compose stack.
 * @property surface Which app a session belongs to — AL-08 scopes it by this.
 * @property app Which of the two on-device databases this app may open (§0.2).
 * @property keychainService Keychain service name, namespaced per surface.
 */
public data class IosAppConfig(
    val baseUrl: String,
    val appVersion: String,
    val userAgent: String,
    val debug: Boolean,
    val mqttHost: String,
    val mqttPort: Int,
    val mqttTls: Boolean,
    val surface: AppSurface,
    val app: MageRideApp,
    val keychainService: String,
) {

    /**
     * The C013 client configuration.
     *
     * `logLevel` is [ApiLogLevel.NONE] in release and never higher than [ApiLogLevel.HEADERS]
     * anywhere: `BODY` would put OTPs, phone numbers and bearer tokens in the device log.
     */
    public val api: ApiConfig get() = ApiConfig(
        baseUrl = baseUrl,
        appVersion = appVersion,
        platform = ClientPlatform.IOS,
        logLevel = if (debug) ApiLogLevel.HEADERS else ApiLogLevel.NONE,
        userAgent = userAgent,
    )

    /** The C014 session configuration. Every timing is left at its documented default. */
    public val auth: AuthConfig get() = AuthConfig(app = surface)

    /** The D6' §3 position plane. Everything except host/port/TLS is the spec's default. */
    public val mqtt: MqttConfig get() = MqttConfig(host = mqttHost, port = mqttPort, useTls = mqttTls)
}

/**
 * The app-supplied half of the graph, as a Koin [Module].
 *
 * Appended after `sharedModules`, so every definition here wins over a shared one — which is what
 * makes the [AttestationProvider] swap a value rather than an edit inside `:shared`.
 *
 * **No `H3Grid` is bound here.** `geoRealtimeModule` throws on resolution when the platform has none
 * (`platformH3Grid()` is `null` on iOS), and that is left as it is for the driver app: AL-31's home
 * map joins no geocell group at all — it shows only the driver's own vehicle — so nothing in it
 * resolves one, and a binding written here would be untested code standing in for a decision. A
 * passenger app does need one (R-06's 19-cell view) and supplies it through [iosH3Module] — see
 * [startIosGraphWithH3].
 */
public fun iosAppModule(config: IosAppConfig): Module = module {
    single { config }

    // C013 — Darwin rather than CIO: it is the engine `iosMain` already depends on, and it goes
    // through the system URLSession, so a proxy, a VPN and ATS all behave the way the OS says.
    single<HttpClientEngine> { Darwin.create() }
    single { config.api }

    // C014 — the surface claim, and the Keychain the tokens live in.
    single { config.auth }
    single<SecureStore> { PlatformSecureStore(config.keychainService) }

    // D-30. Bound as the concrete type too: `prepareRegistration` is iOS's alone, and it is what an
    // app calls once per install. `AttestationProvider` is what C013's request pipeline resolves.
    single { PlatformAttestationProvider(keyStore = get<SecureStore>()) }
    single<AttestationProvider> { get<PlatformAttestationProvider>() }

    // C018 — one file and one table set per app. iOS needs no context; the file lands in the app
    // container under NSFileProtection rather than SQLCipher (§0.4 allows either).
    single { config.app }
    single<DatabaseDriverFactory> { PlatformDatabaseDriverFactory() }

    // C017 / D6' §3. One config for the process; the socket belongs to the app's publisher.
    single { config.mqtt }
}

/**
 * The pieces of the graph Swift actually holds, resolved once.
 *
 * Swift cannot call `koin.get()` (reified), so the resolution happens here and Swift sees typed
 * properties. This is a **view onto the graph, not a second one** — every value below is the same
 * instance Koin hands to `:shared`'s own collaborators.
 *
 * Deliberately small: it carries what an app *shell* needs and nothing a screen invents. A screen
 * group that needs another shared singleton adds a property here rather than starting a second
 * Koin.
 *
 * @property koin The application's Koin instance, for anything not surfaced below.
 * @property api All sixteen typed clients (`MageRideApi` is the bundle built for Swift).
 * @property signals D-31's `upgradeRequired`, which the update gate is the single subscriber to.
 * @property sessions C014's session manager — the only thing that touches a token.
 * @property mqttSessions E-02's MQTT credential, on its own TTL and its own renewal loop.
 * @property secureStore Keychain, for anything the app must persist that is not a database row.
 * @property attestation App Attest. Kept as the concrete type for `prepareRegistration`.
 * @property databases C018's factory. Opening a database is `suspend`; the app owns the handle.
 * @property offers ADD Appendix B.2 invariant 3 — the driver's single offer slot.
 * @property config The values the app supplied, so nothing re-reads them twice.
 * @property upgrades D-31's `426` payload, as something Swift can subscribe to.
 * @property sessionEvents Every way a session can end (C014).
 * @property mqttTokens E-02's credential, absent values dropped — a publisher reconnects on a new
 *   token and stops on `null`, and the two need different call sites.
 */
public class IosAppGraph internal constructor(public val koin: Koin) {
    public val api: MageRideApi = koin.get()
    public val signals: MageRideApiSignals = koin.get()
    public val sessions: AuthSessionManager = koin.get()
    public val mqttSessions: MqttSessionTokenManager = koin.get()
    public val secureStore: SecureStore = koin.get()
    public val attestation: PlatformAttestationProvider = koin.get()
    public val databases: MageRideDatabaseFactory = koin.get()
    public val offers: OfferSession = koin.get()
    public val config: IosAppConfig = koin.get()

    /**
     * The access token the HTTP pipeline sends, for the one caller that is not the pipeline (Δ C094).
     *
     * `/hubs/live` is authenticated with the ordinary 30-minute API access token (D-29, E-02) and
     * carries it in a query parameter, because a browser `WebSocket` cannot set an `Authorization`
     * header. That connection is the app's, not `:shared`'s, so the passenger app's SignalR
     * transport needs the provider — and it must be **this** one: [SessionTokenProvider] reads
     * whatever the manager currently holds, so a socket built on a second instance would be reading
     * a second view of the same rotation.
     *
     * Deliberately the [lk.mageride.shared.data.api.TokenProvider] interface and not the manager: a
     * caller that could reach [AuthSessionManager] could also end a session, and a transport has no
     * business doing that.
     */
    public val tokens: lk.mageride.shared.data.api.TokenProvider = koin.get()

    // The three flows an app *shell* subscribes to. Typed watchers rather than raw `Flow`s because
    // Swift cannot collect one — see IosFlowWatcher.
    public val upgrades: IosFlowWatcher<UpgradeRequiredSignal> = IosFlowWatcher(signals.upgradeRequired)
    public val sessionEvents: IosFlowWatcher<SessionEvent> = IosFlowWatcher(sessions.events)
    public val mqttTokens: IosFlowWatcher<MqttSessionToken> = IosFlowWatcher(mqttSessions.token.filterNotNull())

    /**
     * Where the driver's single offer slot is (C088).
     *
     * SCR-DI-014 is driven by [offers] the way its Android twin is — the slot decides, and the screen
     * renders — and this is the half Swift cannot reach for itself: `OfferSession.state` is a
     * `StateFlow`, [IosFlowWatcher]'s constructor is `internal`, and an app that observed the offer
     * some other way would be a second answer to *"is an offer live"* racing the one that enforces
     * ADD Appendix B.2 invariant 3.
     */
    public val offerStates: IosFlowWatcher<OfferSessionState> = IosFlowWatcher(offers.state)

    /**
     * The idempotency-key generator the HTTP pipeline uses, for the one caller that has to supply
     * its own key (Δ C097).
     *
     * `RideRequest.clientRequestId` **is** the idempotency key: R-18 dedupes a booking on
     * `(passengerId, clientRequestId)`, so a retry after a timeout has to carry the same value or it
     * books a second ride. The key therefore belongs to the screen rather than to the transport, and
     * the screen needs a generator.
     *
     * It must be **this** one rather than a ULID minted in Swift: the contract's own pattern is
     * `^[A-Za-z0-9_-]{16,128}$` and the platform's traces sort by mint time because the value is a
     * ULID, and a second implementation is a second thing that can drift from both.
     */
    public val idempotencyKeys: IdempotencyKeyGenerator = koin.get()
}

/**
 * The app's H3 engine, as a Koin [Module] (Δ C094).
 *
 * **Swift can implement [H3Grid] but cannot write a Koin module**, for the reason this whole file
 * exists: `module { }` and `Module.single` are `inline` + `reified` and are not exported to
 * Objective-C at all. So the app hands over the *instance* and the module is built here.
 *
 * Appended after [iosAppModule], so this definition wins over `geoRealtimeModule`'s throwing
 * default. The engine has to produce ids bit-identical to `position-processor-svc`'s — see
 * [H3Grid]'s own KDoc, and `shared/swiftpm/MageRideH3`, which is what the passenger app passes.
 */
public fun iosH3Module(grid: H3Grid): Module = module {
    single<H3Grid> { grid }
}

/**
 * Starts Koin with `sharedModules` + [iosAppModule] and answers the typed view of it.
 *
 * Called once, from the SwiftUI `App` initialiser. Calling it twice throws — Koin refuses a second
 * `startKoin` — which is the intended behaviour: two graphs would mean two session managers and two
 * refresh loops racing the same single-use refresh token (D-29).
 *
 * This is the **driver** app's entry point, and the absence of an [H3Grid] is deliberate rather
 * than an omission: AL-31's home map joins no geocell group, so nothing in that app resolves one. A
 * passenger app calls [startIosGraphWithH3] instead.
 */
public fun startIosGraph(config: IosAppConfig): IosAppGraph =
    IosAppGraph(initKoin(appModules = listOf(iosAppModule(config))).koin)

/**
 * As [startIosGraph], with the app's own [H3Grid] bound (Δ C094).
 *
 * A second function rather than a defaulted or nullable parameter on the first, and both halves of
 * that matter on this bridge: a Kotlin default argument does not survive the Objective-C export, so
 * a nullable parameter would become **required** at every Swift call site — including the driver
 * app's, which would have to pass an explicit `nil` for an engine it has no use for. Two names, two
 * selectors, and neither call site can be read as the other by mistake.
 */
public fun startIosGraphWithH3(config: IosAppConfig, h3Grid: H3Grid): IosAppGraph =
    IosAppGraph(initKoin(appModules = listOf(iosAppModule(config), iosH3Module(h3Grid))).koin)
