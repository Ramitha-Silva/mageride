package lk.mageride.shared.platform

import java.io.File
import kotlin.test.Test
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * Two rules this component cannot prove any other way, checked against the checked-in source.
 *
 * 1. **"Secrets are never written to plain settings storage in either expect/actual
 *    implementation."** The Android half is exercised for real by `AndroidSecureStoreTest`; the
 *    iOS half cannot be — Kotlin/Native links and runs on macOS only, and this repo is built on
 *    Linux (root `CLAUDE.md`, "Build Host"). What *is* checkable from here is that the iOS store
 *    uses the Keychain, uses a `ThisDeviceOnly` protection class, and never touches
 *    `NSUserDefaults`.
 * 2. **"Apps are Phone-OTP only" (AL-07).** The Google, Apple and password routes exist on
 *    `IamApi` because `iam.yaml` declares them for the portals. Nothing in the mobile session
 *    layer may call one.
 *
 * Like `ModelSourceHygieneTest` (C012) this lives in `androidHostTest` because that is the only
 * source set with a filesystem. Comments are stripped first — the whole point of these files is
 * to *document* why they do not use `NSUserDefaults`, and a check that fired on the explanation
 * would push the explanation out of the code.
 */
class PlatformSecurityHygieneTest {

    // ------------------------------------------------------------------------------------------
    // iOS — the half this host cannot run
    // ------------------------------------------------------------------------------------------

    @Test
    fun the_ios_store_keeps_secrets_in_the_keychain() {
        val source = sourceFile("src/iosMain/kotlin/lk/mageride/shared/platform/PlatformSecureStore.ios.kt")

        assertTrue("kSecClassGenericPassword" in source, "the iOS store must use Keychain items")
        assertTrue("SecItemAdd" in source && "SecItemCopyMatching" in source)
    }

    @Test
    fun every_ios_keychain_item_is_this_device_only() {
        // `ThisDeviceOnly` is what keeps a session out of iCloud Keychain and out of every
        // backup, so a restored backup cannot resume it on another handset.
        val source = sourceFile("src/iosMain/kotlin/lk/mageride/shared/platform/PlatformSecureStore.ios.kt")
        val protectionClasses = ACCESSIBLE_CONSTANT
            .findAll(source)
            .map { it.value }
            .filter { it != "kSecAttrAccessible" }
            .toList()

        assertTrue(protectionClasses.isNotEmpty(), "the store must state a protection class")
        val leaky = protectionClasses.filterNot { it.endsWith("ThisDeviceOnly") }
        assertTrue(leaky.isEmpty(), "these protection classes sync or restore off-device: $leaky")
    }

    @Test
    fun no_platform_store_falls_back_to_plain_settings() {
        PLATFORM_STORES.forEach { relative ->
            val source = sourceFile(relative)
            PLAIN_SETTINGS_APIS.forEach { forbidden ->
                assertTrue(forbidden !in source, "$relative must not reach for $forbidden")
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Android — the structural half AndroidSecureStoreTest cannot see
    // ------------------------------------------------------------------------------------------

    @Test
    fun the_android_store_encrypts_under_a_keystore_key_and_writes_synchronously() {
        val source = sourceFile("src/androidMain/kotlin/lk/mageride/shared/platform/PlatformSecureStore.android.kt")

        assertTrue("AndroidKeyStore" in source, "the key must live in the Keystore, not in the app")
        assertTrue("AES/GCM/NoPadding" in source)
        assertTrue("setRandomizedEncryptionRequired(true)" in source, "a fresh IV per encryption")
        assertTrue("Context.MODE_PRIVATE" in source)
        // commit(), not apply(): the rotated refresh token is written before the in-memory copy
        // moves, and a queued apply() lost to a process death is a forced sign-out.
        assertTrue(".commit()" in source && ".apply()" !in source)
    }

    @Test
    fun the_android_sink_only_ever_receives_sealed_bytes() {
        val source = sourceFile("src/androidMain/kotlin/lk/mageride/shared/platform/PlatformSecureStore.android.kt")
        val writes = PUT_STRING.findAll(source).map { it.groupValues[1].trim() }.toList()

        assertTrue(writes.isNotEmpty(), "the sink still writes something")
        val plaintext = writes.filterNot { it.endsWith(".encoded") }
        assertTrue(plaintext.isEmpty(), "these preference writes are not sealed values: $plaintext")
    }

    // ------------------------------------------------------------------------------------------
    // AL-07 — the mobile auth module is Phone OTP only
    // ------------------------------------------------------------------------------------------

    @Test
    fun the_session_layer_never_calls_a_portal_sign_in() {
        val authDir = moduleDir().resolve("src/commonMain/kotlin/lk/mageride/shared/domain/auth")
        val offenders = authDir
            .walkTopDown()
            .filter { it.isFile && it.extension == "kt" }
            .flatMap { file ->
                val code = codeOf(file.readText())
                PORTAL_SIGN_INS.filter { it in code }.map { "${file.name}: $it" }
            }
            .toList()

        assertTrue(offenders.isEmpty(), "AL-07 — apps are Phone OTP only; found $offenders")
    }

    // ------------------------------------------------------------------------------------------

    private fun sourceFile(relative: String): String {
        val file = File(moduleDir(), relative)
        if (!file.isFile) fail("could not read $relative")
        return codeOf(file.readText())
    }

    /**
     * The file with its comments blanked out.
     *
     * A hand-rolled scanner rather than a regex: the obvious `/\*(?:[^*]|\*(?!/))*\*​/` blows the
     * stack on a file this size, and C012's `ModelSourceHygieneTest` reached the same conclusion
     * from the other direction. Deliberately naive about string literals — safe for exactly these
     * files, none of which contains a comment opener inside a literal.
     */
    private fun codeOf(source: String): String {
        val code = StringBuilder(source.length)
        var index = 0
        var depth = 0
        while (index < source.length) {
            val rest = source.length - index
            when {
                rest >= 2 && source.startsWith("/*", index) -> {
                    depth++
                    index += 2
                }

                depth > 0 && rest >= 2 && source.startsWith("*/", index) -> {
                    depth--
                    index += 2
                }

                depth > 0 -> index++

                rest >= 2 && source.startsWith("//", index) -> {
                    while (index < source.length && source[index] != '\n') index++
                }

                else -> code.append(source[index++])
            }
        }
        return code.toString()
    }

    /**
     * `shared/kmp`, whether the test runs from Gradle (working directory = the module) or from an
     * IDE (often the repository root).
     */
    private fun moduleDir(): File {
        val workingDir = requireNotNull(System.getProperty("user.dir")) { "user.dir is unset" }
        var dir: File? = File(workingDir).absoluteFile
        while (dir != null) {
            if (File(dir, MARKER).isFile) return dir
            val nested = File(dir, "shared/kmp")
            if (File(nested, MARKER).isFile) return nested
            dir = dir.parentFile
        }
        fail("could not locate the shared module from $workingDir")
    }

    private companion object {
        const val MARKER = "src/commonMain/kotlin/lk/mageride/shared/platform/SecureStore.kt"

        val PLATFORM_STORES = listOf(
            "src/androidMain/kotlin/lk/mageride/shared/platform/PlatformSecureStore.android.kt",
            "src/iosMain/kotlin/lk/mageride/shared/platform/PlatformSecureStore.ios.kt",
        )

        /** Stores that are readable off-device, or by anything with the file. */
        val PLAIN_SETTINGS_APIS = listOf(
            "NSUserDefaults",
            "com.russhwolf.settings",
            "MODE_WORLD_READABLE",
            "MODE_WORLD_WRITEABLE",
        )

        val PORTAL_SIGN_INS = listOf(
            "signInWithGoogle",
            "signInWithApple",
            "signInWithPassword",
            "adminLoginWith",
        )

        val ACCESSIBLE_CONSTANT = Regex("kSecAttrAccessible[A-Za-z]*")
        val PUT_STRING = Regex("""putString\([^,]+,([^)]*)\)""")
    }
}
