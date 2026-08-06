package lk.mageride.shared.db

import android.content.Context
import app.cash.sqldelight.db.SqlDriver
import app.cash.sqldelight.driver.android.AndroidSqliteDriver
import net.zetetic.database.sqlcipher.SupportOpenHelperFactory

/**
 * Android's SQLite connection — SQLCipher when a passphrase is supplied, the framework engine
 * when it is not.
 *
 * `mobile_db_schema.md` §0.4: "The database file SHOULD be encrypted at rest with **SQLCipher**
 * (Android) …; the key is wrapped by the hardware keystore." Both halves are here: the cipher is
 * `net.zetetic:sqlcipher-android` and the key comes from [DatabaseKeyManager] over C014's
 * Keystore-backed [lk.mageride.shared.platform.SecureStore].
 *
 * **SQLCipher also raises the engine.** It links its own SQLite (3.4x) rather than the platform's,
 * which on the URD NFR-22 floor (API 26 / Android 8.0) is 3.19. The schema and the migrations are
 * still written to the 3.19 feature set — no `RENAME COLUMN`, no UPSERT, no row-value `IN` —
 * because an unencrypted build falls through to `FrameworkSQLiteOpenHelperFactory` and gets
 * whatever the handset ships.
 *
 * @param context Any context; the application context is taken from it, so holding this factory
 *   for the process lifetime cannot leak an Activity.
 */
public actual class PlatformDatabaseDriverFactory(context: Context) : DatabaseDriverFactory {

    private val app: Context = context.applicationContext

    actual override fun create(request: DatabaseRequest): SqlDriver {
        // A null name is an in-memory database — the SQLDelight/AndroidX contract, and how an
        // `inMemory` request is expressed without a second code path.
        val name = if (request.inMemory) null else request.app.databaseName
        val passphrase = request.passphrase
            ?: return AndroidSqliteDriver(schema = request.schema, context = app, name = name)

        loadCipher()
        return AndroidSqliteDriver(
            schema = request.schema,
            context = app,
            name = name,
            // SQLCipher takes ownership of the array and zeroes it itself, so clearing our copy
            // afterwards would be clearing an array it has already wiped.
            factory = SupportOpenHelperFactory(passphrase.bytes),
        )
    }

    actual override fun delete(app: MageRideApp): Boolean = this.app.deleteDatabase(app.databaseName)

    private companion object {
        /**
         * Loads libsqlcipher before any SQLCipher class is touched.
         *
         * Called from [create] rather than a class initialiser so that an unencrypted open — a
         * host unit test, a bring-up build — never tries to load a native library that is not
         * there. `System.loadLibrary` is idempotent.
         */
        fun loadCipher() {
            System.loadLibrary("sqlcipher")
        }
    }
}
