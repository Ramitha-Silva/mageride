package lk.mageride.shared.domain.auth

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.serialization.json.Json
import lk.mageride.shared.data.api.TokenProvider
import lk.mageride.shared.data.api.iam.IamApi
import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * The C014 slice of the Koin graph.
 *
 * **Two bindings the app must supply**, for the same reason C013 needs an engine and an
 * `ApiConfig` — `commonMain` cannot know them:
 * - [AuthConfig] — which surface this build is (AL-08). There is no safe default.
 * - [lk.mageride.shared.platform.SecureStore] — `PlatformSecureStore(context, namespace)` on
 *   Android, `PlatformSecureStore(service)` on iOS. See `shared/kmp/CLAUDE.md`.
 *
 * This module **overrides C013's [TokenProvider.Anonymous]** with the real one. Koin's later
 * definition wins and [lk.mageride.shared.di.sharedModules] lists `apiModule` first, so nothing in
 * an app has to know the swap happened.
 *
 * [lk.mageride.shared.data.api.AttestationProvider] is deliberately **not** bound here: it needs
 * platform configuration (a Play Console cloud project number, an App Attest key) that only the
 * app has. C067/C076 and C085/C094 bind `PlatformAttestationProvider`; until they do, C013's
 * `Unavailable` default stands and the twenty attested operations fail honestly at the edge.
 */
public val authModule: Module = module {
    single { AuthSessionStore(secure = get(), config = get(), json = get<Json>()) }

    single {
        // `get<IamApi>()` is deferred, not eager: IamApi → HttpClient → TokenProvider → this.
        // Resolving it here would be a cycle; resolving it at first use is just a lookup.
        val scope = this
        AuthSessionManager(api = { scope.get<IamApi>() }, store = get(), config = get())
    }

    single<TokenProvider> { SessionTokenProvider(get()) }

    single {
        val scope = this
        MqttSessionTokenManager(
            api = { scope.get<IamApi>() },
            sessions = get(),
            store = get(),
            config = get(),
            // Its own scope rather than a `CoroutineScope` binding in the graph: exactly one
            // object needs it, and a bare CoroutineScope in a shared graph is something every
            // later component would end up cancelling on someone else's behalf.
            scope = CoroutineScope(SupervisorJob() + Dispatchers.Default),
        )
    }
}
