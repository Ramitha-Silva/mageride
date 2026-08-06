package lk.mageride.shared.db

import app.cash.sqldelight.db.SqlDriver
import app.cash.sqldelight.driver.jdbc.sqlite.JdbcSqliteDriver
import java.io.File

/**
 * The JVM's driver: xerial SQLite over JDBC — a real SQLite engine, the same one
 * `androidHostTest` opens.
 *
 * **No SQLCipher.** `mobile_db_schema.md` §0.4 encrypts the database file on a handset and wraps
 * the key with the hardware keystore; a JVM has no keystore (see
 * [lk.mageride.shared.platform.PlatformSecureStore]) and no user's data on it either. A
 * [DatabaseRequest.passphrase] is therefore **rejected** rather than quietly ignored — silently
 * opening an unencrypted file for a caller that asked for an encrypted one is the failure mode
 * §0.4 exists to prevent.
 *
 * @param directory Where a file-backed database lives. Defaults to the working directory, which
 *   is what a harness or a test wants; nothing on this target has an app-private storage path.
 */
public actual class PlatformDatabaseDriverFactory(private val directory: File = File(".")) : DatabaseDriverFactory {

    actual override fun create(request: DatabaseRequest): SqlDriver {
        require(request.passphrase == null) {
            "The JVM driver cannot encrypt: mobile_db_schema.md §0.4's SQLCipher key is wrapped by " +
                "a hardware keystore this target does not have. Open it in memory or unencrypted, " +
                "deliberately."
        }

        val url = if (request.inMemory) {
            JdbcSqliteDriver.IN_MEMORY
        } else {
            "jdbc:sqlite:${File(directory, request.app.databaseName).absolutePath}"
        }

        val driver = JdbcSqliteDriver(url)

        // JdbcSqliteDriver does not create or migrate on its own, unlike the Android and Native
        // drivers — the schema has to be applied here or every query fails on a missing table.
        // An existing file is left alone; SqlDriver has no version to compare, so a migration on
        // this target is the caller's business (nothing on it is long-lived by design).
        if (request.inMemory || !File(directory, request.app.databaseName).exists()) {
            request.schema.create(driver).value
        }

        return driver
    }

    actual override fun delete(app: MageRideApp): Boolean = File(directory, app.databaseName).delete()
}
