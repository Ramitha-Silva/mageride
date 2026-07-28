package lk.mageride.shared.db

import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.usePinned
import platform.Security.SecRandomCopyBytes
import platform.Security.errSecSuccess
import platform.Security.kSecRandomDefault
import platform.posix.size_t

/**
 * `SecRandomCopyBytes` — the CSPRNG iOS itself uses for key material.
 *
 * Throws rather than falling back if the system declines: a database key from a degraded source is
 * worse than a database that will not open, because only one of the two is noticed.
 */
@OptIn(ExperimentalForeignApi::class)
public actual fun secureRandomBytes(size: Int): ByteArray {
    val bytes = ByteArray(size)
    val status = bytes.usePinned { pinned ->
        SecRandomCopyBytes(kSecRandomDefault, size.toULong() as size_t, pinned.addressOf(0))
    }
    check(status == errSecSuccess) { "SecRandomCopyBytes failed with status $status" }
    return bytes
}
