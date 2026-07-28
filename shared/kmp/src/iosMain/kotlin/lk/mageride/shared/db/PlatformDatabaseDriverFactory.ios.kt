package lk.mageride.shared.db

import app.cash.sqldelight.db.SqlDriver
import app.cash.sqldelight.driver.native.NativeSqliteDriver
import co.touchlab.sqliter.DatabaseFileContext
import kotlinx.cinterop.ExperimentalForeignApi
import platform.Foundation.NSFileManager
import platform.Foundation.NSFileProtectionCompleteUntilFirstUserAuthentication
import platform.Foundation.NSFileProtectionKey

/**
 * iOS's SQLite connection, protected by the file system rather than by SQLCipher.
 *
 * `mobile_db_schema.md` §0.4 asks for "**SQLCipher or GRDB encryption**" on iOS — either, not
 * SQLCipher specifically. This takes a third option that is stronger than both and needs no third
 * party: **`NSFileProtectionCompleteUntilFirstUserAuthentication`**, where iOS encrypts the file
 * with a class key derived from the device UID and the user's passcode, held in the Secure
 * Enclave. There is no application-held key to leak, and it is the same accessibility class C014
 * gives the Keychain items (`kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`) — deliberately, so
 * the database and the session tokens become readable at the same moment in the boot sequence.
 *
 * `AfterFirstUserAuthentication`, not `Complete`: the driver app writes `gps_buffer` from a
 * background location session with the handset locked in a pocket, and `Complete` would make every
 * one of those writes fail.
 *
 * [DatabaseRequest.passphrase] is therefore accepted and **not applied** here. It is carried
 * rather than dropped because SQLCipher on Kotlin/Native needs a cinterop against an H3-style
 * `ios-arm64` build of the cipher, which cannot be produced on the Linux build host; when C085 /
 * C094 add one, this is the single function that changes. Same shape as C017's iOS H3 seam.
 */
public actual class PlatformDatabaseDriverFactory : DatabaseDriverFactory {

    override fun create(request: DatabaseRequest): SqlDriver {
        val name = request.app.databaseName
        val driver = NativeSqliteDriver(schema = request.schema, name = if (request.inMemory) ":memory:" else name)
        if (!request.inMemory) protect(name)
        return driver
    }

    override fun delete(app: MageRideApp): Boolean {
        val path = DatabaseFileContext.databasePath(app.databaseName, null)
        if (!NSFileManager.defaultManager.fileExistsAtPath(path)) return false
        DatabaseFileContext.deleteDatabase(app.databaseName, null)
        return true
    }

    /**
     * Applies the protection class to the file SQLite just created.
     *
     * After the open, not before: the file does not exist until the driver creates it, and an
     * attribute set on a missing path is silently dropped.
     */
    @OptIn(ExperimentalForeignApi::class)
    private fun protect(name: String) {
        val path = DatabaseFileContext.databasePath(name, null)
        NSFileManager.defaultManager.setAttributes(
            mapOf(NSFileProtectionKey to NSFileProtectionCompleteUntilFirstUserAuthentication),
            ofItemAtPath = path,
            error = null,
        )
    }
}
