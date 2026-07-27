package lk.mageride.shared.platform

import kotlinx.cinterop.BetaInteropApi
import kotlinx.cinterop.COpaquePointerVar
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.alloc
import kotlinx.cinterop.allocArray
import kotlinx.cinterop.convert
import kotlinx.cinterop.get
import kotlinx.cinterop.memScoped
import kotlinx.cinterop.ptr
import kotlinx.cinterop.set
import kotlinx.cinterop.usePinned
import kotlinx.cinterop.value
import platform.CoreFoundation.CFDictionaryCreate
import platform.CoreFoundation.CFDictionaryRef
import platform.CoreFoundation.CFRelease
import platform.CoreFoundation.CFStringRef
import platform.CoreFoundation.CFTypeRef
import platform.CoreFoundation.CFTypeRefVar
import platform.CoreFoundation.kCFBooleanTrue
import platform.CoreFoundation.kCFTypeDictionaryKeyCallBacks
import platform.CoreFoundation.kCFTypeDictionaryValueCallBacks
import platform.Foundation.CFBridgingRelease
import platform.Foundation.CFBridgingRetain
import platform.Foundation.NSData
import platform.Foundation.create
import platform.Security.SecItemAdd
import platform.Security.SecItemCopyMatching
import platform.Security.SecItemDelete
import platform.Security.SecItemUpdate
import platform.Security.errSecDuplicateItem
import platform.Security.errSecSuccess
import platform.Security.kSecAttrAccessible
import platform.Security.kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
import platform.Security.kSecAttrAccount
import platform.Security.kSecAttrService
import platform.Security.kSecClass
import platform.Security.kSecClassGenericPassword
import platform.Security.kSecMatchLimit
import platform.Security.kSecMatchLimitOne
import platform.Security.kSecReturnData
import platform.Security.kSecValueData
import platform.posix.memcpy

/**
 * The iOS half of [SecureStore]: `kSecClassGenericPassword` items in the **Keychain**.
 *
 * ADD §12.1 asks for "iOS Keychain + Secure Enclave" device binding, and this is what that means
 * for stored bytes: a Keychain item's data-protection class key is wrapped by the Secure Enclave's
 * UID key, so the ciphertext on disk is bound to the hardware. Nothing here hand-rolls
 * cryptography, which is the point — an app-level AES layer over the Keychain would add a key the
 * app has to protect, and the Keychain exists so it does not have to.
 *
 * Two attributes carry the whole security argument:
 *
 * - **`kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`.** `ThisDeviceOnly` keeps the item out of
 *   iCloud Keychain and out of every backup, so a restored backup cannot resume a session on
 *   another handset. `AfterFirstUnlock` rather than `WhenUnlocked` because a driver's phone is
 *   locked in a mount for most of a ride and the MQTT renewal loop (E-02) must be able to read its
 *   credential then — `WhenUnlocked` would make the one token designed to survive a long trip
 *   unreadable during it.
 * - **`kSecAttrService`.** Namespaced per surface, so the driver and passenger apps cannot see
 *   each other's session (AL-08).
 *
 * `NSUserDefaults` is never touched — see `PlatformSecureStoreSourceTest`.
 *
 * @param service Keychain service name. C014 passes the surface-scoped namespace.
 */
@OptIn(ExperimentalForeignApi::class)
public actual class PlatformSecureStore(private val service: String) : SecureStore {

    override suspend fun read(key: String): String? = withBridged(service, key) { cfService, cfAccount ->
        withQuery(
            kSecClass to kSecClassGenericPassword,
            kSecAttrService to cfService,
            kSecAttrAccount to cfAccount,
            kSecReturnData to kCFBooleanTrue,
            kSecMatchLimit to kSecMatchLimitOne,
        ) { query ->
            memScoped {
                val found = alloc<CFTypeRefVar>()
                if (SecItemCopyMatching(query, found.ptr) != errSecSuccess) {
                    null
                } else {
                    (CFBridgingRelease(found.value) as? NSData)?.toKotlinString()
                }
            }
        }
    }

    override suspend fun write(key: String, value: String) {
        val cfData = CFBridgingRetain(value.toNSData())
        try {
            withBridged(service, key) { cfService, cfAccount ->
                addOrUpdate(cfService, cfAccount, cfData)
            }
        } finally {
            cfData?.let { CFRelease(it) }
        }
    }

    override suspend fun delete(key: String) {
        withBridged(service, key) { cfService, cfAccount ->
            withQuery(
                kSecClass to kSecClassGenericPassword,
                kSecAttrService to cfService,
                kSecAttrAccount to cfAccount,
            ) { query -> SecItemDelete(query) }
        }
    }

    /** Deletes every item under this service — the whole namespace, in one call. */
    override suspend fun clear() {
        val cfService = CFBridgingRetain(service)
        try {
            withQuery(
                kSecClass to kSecClassGenericPassword,
                kSecAttrService to cfService,
            ) { query -> SecItemDelete(query) }
        } finally {
            cfService?.let { CFRelease(it) }
        }
    }

    /**
     * Adds the item, or replaces its data when one is already there.
     *
     * Add-then-update rather than delete-then-add: a delete that succeeded followed by an add that
     * failed would leave no session at all, where a failed update leaves the previous one intact.
     */
    private fun addOrUpdate(cfService: CFTypeRef?, cfAccount: CFTypeRef?, cfData: CFTypeRef?) {
        val added = withQuery(
            kSecClass to kSecClassGenericPassword,
            kSecAttrService to cfService,
            kSecAttrAccount to cfAccount,
            kSecValueData to cfData,
            kSecAttrAccessible to kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ) { query -> SecItemAdd(query, null) }

        if (added != errSecDuplicateItem) return

        withQuery(
            kSecClass to kSecClassGenericPassword,
            kSecAttrService to cfService,
            kSecAttrAccount to cfAccount,
        ) { query ->
            withQuery(kSecValueData to cfData) { changes -> SecItemUpdate(query, changes) }
        }
    }
}

/** Bridges the service and account strings once, and releases them however [block] leaves. */
@OptIn(ExperimentalForeignApi::class)
private inline fun <T> withBridged(service: String, account: String, block: (CFTypeRef?, CFTypeRef?) -> T): T {
    val cfService = CFBridgingRetain(service)
    val cfAccount = CFBridgingRetain(account)
    return try {
        block(cfService, cfAccount)
    } finally {
        cfService?.let { CFRelease(it) }
        cfAccount?.let { CFRelease(it) }
    }
}

/** Builds a Keychain query dictionary, hands it to [block], and releases it. */
@OptIn(ExperimentalForeignApi::class)
private inline fun <T> withQuery(vararg pairs: Pair<CFStringRef?, CFTypeRef?>, block: (CFDictionaryRef?) -> T): T {
    val dictionary = memScoped {
        val keys = allocArray<COpaquePointerVar>(pairs.size)
        val values = allocArray<COpaquePointerVar>(pairs.size)
        pairs.forEachIndexed { index, pair ->
            keys[index] = pair.first
            values[index] = pair.second
        }
        CFDictionaryCreate(
            null,
            keys,
            values,
            pairs.size.convert(),
            kCFTypeDictionaryKeyCallBacks.ptr,
            kCFTypeDictionaryValueCallBacks.ptr,
        )
    }
    return try {
        block(dictionary)
    } finally {
        dictionary?.let { CFRelease(it) }
    }
}

/**
 * Bytes in, bytes out — no `NSString` bridging in either direction.
 *
 * `NSString.create(data:encoding:)` followed by a cast to Kotlin's `String` compiles and is always
 * null at runtime; copying the bytes and decoding them is both correct and one hop shorter.
 */
@OptIn(ExperimentalForeignApi::class, BetaInteropApi::class)
private fun String.toNSData(): NSData {
    val bytes = encodeToByteArray()
    if (bytes.isEmpty()) return NSData()
    return bytes.usePinned { pinned ->
        NSData.create(bytes = pinned.addressOf(0), length = bytes.size.convert())
    }
}

@OptIn(ExperimentalForeignApi::class)
private fun NSData.toKotlinString(): String? {
    val size = length.toInt()
    if (size == 0) return ""
    val bytes = ByteArray(size)
    bytes.usePinned { pinned -> memcpy(pinned.addressOf(0), this.bytes, length) }
    return bytes.decodeToString()
}
