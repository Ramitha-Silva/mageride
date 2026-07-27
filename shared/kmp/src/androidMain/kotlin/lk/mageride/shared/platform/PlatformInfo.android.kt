package lk.mageride.shared.platform

import android.os.Build

private const val UNKNOWN = "unknown"

/**
 * `Build` is a stub in a local unit test, so every field can come back null even though the
 * Java type says otherwise — the host-test `isReturnDefaultValues` in build.gradle.kts is what
 * keeps it from throwing. The orEmpty/ifBlank chain keeps the [PlatformInfo.deviceModel]
 * "never blank" promise on a JVM as well as on a device.
 */
public actual fun platformInfo(): PlatformInfo = PlatformInfo(
    os = "Android",
    osVersion = Build.VERSION.RELEASE.orEmpty().ifBlank { UNKNOWN },
    deviceModel = listOf(Build.MANUFACTURER, Build.MODEL)
        .mapNotNull { it?.trim()?.takeIf(String::isNotEmpty) }
        .joinToString(" ")
        .ifBlank { UNKNOWN },
)
