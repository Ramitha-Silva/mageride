package lk.mageride.shared.platform

private const val UNKNOWN = "unknown"

/**
 * The JVM's own identity, from the standard system properties.
 *
 * [PlatformInfo.os] is deliberately `"JVM"` and not `"Android"`. It reaches the wire as the
 * `X-Platform` header via [lk.mageride.shared.data.models.ClientPlatform], where D-31's
 * minimum-version gate reads it, and a harness that claimed to be an Android build would be
 * asking the gateway to apply the Play Store's version floor to a program that has no store
 * listing. The e2e harness sets `ClientPlatform` explicitly on its `ApiConfig` for that reason —
 * this value is for logs and diagnostics.
 */
public actual fun platformInfo(): PlatformInfo = PlatformInfo(
    os = "JVM",
    osVersion = System.getProperty("java.version").orEmpty().ifBlank { UNKNOWN },
    deviceModel = listOf(System.getProperty("os.name"), System.getProperty("os.arch"))
        .mapNotNull { it?.trim()?.takeIf(String::isNotEmpty) }
        .joinToString(" ")
        .ifBlank { UNKNOWN },
)
