package lk.mageride.passenger.di

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.db.MageRideDatabaseFactory
import lk.mageride.shared.db.passenger.PassengerDb

/**
 * The one open `mageride_passenger.db`, for the whole process.
 *
 * **C018 deliberately binds no open database** — opening one is `suspend` because the SQLCipher key
 * comes out of the Android Keystore, and Koin has no suspending factory, so a `single { runBlocking
 * { … } }` would put a Keystore round trip on `Application.onCreate`. `localDbModule`'s KDoc says
 * the app should *"open the database once during start-up and bind the result in its own module"*.
 * This is that binding, deferred: the first caller opens it, everybody after that gets the same
 * handle, and an app that never touches the local tables never pays for the key at all.
 *
 * **One handle, not one per caller.** `mobile_db_schema.md` §2 gives this app six tables and every
 * screen group from C077 on will read one of them — saved addresses, place recents, the ride
 * projection, cached estimates, the proxy location requests, the block list. Six `openPassenger()`
 * calls would be six SQLite connections to one encrypted file, each with its own write lock. The
 * [Mutex] is what makes "first caller opens it" true when two of them arrive at once; it guards the
 * *open*, not the queries, which are the driver's own business. This is the shape the C075 handoff
 * asked this component to copy, and the mistake it asked it not to repeat.
 *
 * **Nothing in the shell reads it yet**, and that is deliberate rather than an omission: the shell
 * owns no screen and has no table of its own. It is bound here because DI is this component's
 * deliverable and because the alternative — each of C077–C084 opening the file itself — is exactly
 * the bug above.
 *
 * The handle is never closed. Its lifetime is the process's, and closing it would be closing it
 * under whichever screen happens to be reading.
 */
internal class PassengerDatabase(private val factory: MageRideDatabaseFactory) {

    private val mutex = Mutex()

    @Volatile
    private var opened: PassengerDb? = null

    /**
     * The database, opening it on the first call.
     *
     * **Every query on the result blocks** (`MageRideDb`'s KDoc) — SQLDelight's Android driver is
     * synchronous — so a caller runs them off the main thread with `withContext(Dispatchers.IO)`.
     * A view model must not call this from a composition.
     */
    suspend fun get(): PassengerDb = opened ?: mutex.withLock {
        opened ?: factory.openPassenger().also { opened = it }
    }
}
