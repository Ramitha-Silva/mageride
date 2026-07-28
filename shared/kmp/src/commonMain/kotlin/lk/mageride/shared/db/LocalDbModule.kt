package lk.mageride.shared.db

import lk.mageride.shared.platform.SecureStore
import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * C018's Koin bindings — two, and neither of them is a database.
 *
 * [DatabaseKeyManager] and [MageRideDatabaseFactory] resolve from things the graph already has:
 * the [SecureStore] C014 requires, plus a [DatabaseDriverFactory] the **app** must bind, because
 * only the app has the Android `Context` and only the app knows whether it is the passenger or the
 * driver surface. C067 / C076 bind `PlatformDatabaseDriverFactory(context)`; C085 / C094 bind
 * `PlatformDatabaseDriverFactory()`.
 *
 * **The open [MageRideDb] is deliberately not bound here.** Opening it is `suspend` — the
 * SQLCipher key comes out of the Keystore/Keychain — and Koin has no suspending factory, so a
 * `single { runBlocking { … } }` would be the only way to express it: a Keystore round trip on
 * whatever thread first touched the graph, which on Android is the main thread during
 * `Application.onCreate`. The app opens the database once during start-up and binds the result in
 * its own module, which is also where it can show a splash while the key is unwrapped.
 *
 * `keys` is nullable in [MageRideDatabaseFactory] so a test can open unencrypted; the binding here
 * always supplies one, so an app that takes the default gets an encrypted file (§0.4).
 */
public val localDbModule: Module = module {
    single { DatabaseKeyManager(secure = get<SecureStore>()) }
    single { MageRideDatabaseFactory(drivers = get<DatabaseDriverFactory>(), keys = get<DatabaseKeyManager>()) }
}
