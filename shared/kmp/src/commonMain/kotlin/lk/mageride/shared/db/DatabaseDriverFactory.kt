package lk.mageride.shared.db

import app.cash.sqldelight.db.QueryResult
import app.cash.sqldelight.db.SqlDriver
import app.cash.sqldelight.db.SqlSchema
import kotlin.Unit as KotlinUnit

/**
 * Everything the platform needs to open one of the two on-device databases.
 *
 * @property app Which database — [MageRideApp.databaseName] is the file (§0.2).
 * @property schema The generated `MageRide*Database.Schema`. Creating at version N and migrating
 *   from N to N+1 are both this object's job; the driver only has to hand it the connection.
 * @property passphrase The SQLCipher key (§0.4), or `null` for an unencrypted file. `null` is for
 *   tests and for a platform with no cipher available — never a production default; see
 *   [DatabaseKeyManager].
 * @property inMemory Opens a throwaway database with no file at all. Tests only.
 */
public data class DatabaseRequest(
    val app: MageRideApp,
    val schema: SqlSchema<QueryResult.Value<KotlinUnit>>,
    val passphrase: DatabasePassphrase? = null,
    val inMemory: Boolean = false,
)

/**
 * Opens the SQLite connection the generated database runs on.
 *
 * A seam rather than a direct `AndroidSqliteDriver` / `NativeSqliteDriver` call, because the two
 * platforms disagree about more than the constructor: Android encrypts the file with SQLCipher
 * and iOS relies on NSFileProtection (§0.4 allows either), and only the platform knows where an
 * app's private storage is. [PlatformDatabaseDriverFactory] is the real one; a test binds its own.
 */
public interface DatabaseDriverFactory {

    /** Opens (creating or migrating as needed) the database described by [request]. */
    public fun create(request: DatabaseRequest): SqlDriver

    /**
     * Deletes the database FILE for [app], if there is one.
     *
     * This is the §0.4 wipe — logout, `403 device-revoked` (AL-08) and PDPA erasure all take the
     * whole file rather than emptying tables, because an emptied SQLite file still holds the old
     * pages until they are overwritten. Close the database first. [MageRideDb.wipe] is the
     * in-place fallback for when the app cannot close it.
     *
     * @return whether a file was removed.
     */
    public fun delete(app: MageRideApp): Boolean
}

/**
 * The platform's real driver factory: SQLCipher-backed on Android, NSFileProtection-backed on iOS.
 *
 * `expect class` with no common constructor, exactly as [lk.mageride.shared.platform.SecureStore]'s
 * [lk.mageride.shared.platform.PlatformSecureStore] — Android needs a `Context` to find the app's
 * private database directory, iOS needs nothing. C067 / C076 construct it with a context, C085 /
 * C094 with no arguments, and `commonMain` only ever sees [DatabaseDriverFactory].
 *
 * Both members are re-declared rather than only inherited, for the reason
 * [lk.mageride.shared.platform.PlatformSecureStore]'s KDoc gives (Δ C085).
 */
public expect class PlatformDatabaseDriverFactory : DatabaseDriverFactory {

    override fun create(request: DatabaseRequest): SqlDriver

    override fun delete(app: MageRideApp): Boolean
}
