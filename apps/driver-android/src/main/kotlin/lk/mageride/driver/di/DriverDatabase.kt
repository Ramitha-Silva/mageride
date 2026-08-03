package lk.mageride.driver.di

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.db.MageRideDatabaseFactory
import lk.mageride.shared.db.driver.DriverDb

/**
 * The one open `mageride_driver.db`, for the whole process.
 *
 * **C018 deliberately binds no open database** — opening one is `suspend` because the SQLCipher key
 * comes out of the Android Keystore, and Koin has no suspending factory, so a `single { runBlocking
 * { … } }` would put a Keystore round trip on `Application.onCreate`. `localDbModule`'s KDoc says
 * the app should *"open the database once during start-up and bind the result in its own module"*.
 * This is that binding, deferred: the first caller opens it, everybody after that gets the same
 * handle, and an app that never touches the local tables never pays for the key at all.
 *
 * **One handle, not one per caller** (Δ C075). Three things now read this file — the position
 * service's GPS ring (C067), SCR-DA-034's alert inbox and SCR-DA-035's buffered-sample count — and
 * three `openDriver()` calls would be three SQLite connections to one encrypted file, each with its
 * own write lock. The [Mutex] is what makes "first caller opens it" true when two of them arrive at
 * once; it guards the *open*, not the queries, which are the driver's own business.
 *
 * The handle is never closed. Its lifetime is the process's, which is also `PositionForegroundService`'s
 * and `DriverMessagingService`'s — closing it under either would be closing it under a ride.
 */
internal class DriverDatabase(private val factory: MageRideDatabaseFactory) {

    private val mutex = Mutex()

    @Volatile
    private var opened: DriverDb? = null

    /**
     * The database, opening it on the first call.
     *
     * **Every query on the result blocks** (`MageRideDb`'s KDoc) — SQLDelight's Android driver is
     * synchronous — so a caller runs them off the main thread. The repositories in this app do that
     * with `withContext(Dispatchers.IO)`; a view model must not call this from a composition.
     */
    suspend fun get(): DriverDb = opened ?: mutex.withLock {
        opened ?: factory.openDriver().also { opened = it }
    }
}
