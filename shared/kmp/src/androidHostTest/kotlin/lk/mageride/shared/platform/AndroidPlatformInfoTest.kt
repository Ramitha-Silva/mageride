package lk.mageride.shared.platform

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Pins the Android `actual`. `android.os.Build` is a stub in a local unit test and every field
 * comes back null, which is exactly the degenerate case the actual has to survive without
 * breaking [PlatformInfo]'s "never blank" promise.
 */
class AndroidPlatformInfoTest {
    @Test
    fun android_actual_never_reports_a_blank_field() {
        val info = platformInfo()

        assertEquals("Android", info.os)
        assertEquals("android", info.clientPlatform)
        assertTrue(info.osVersion.isNotBlank())
        assertTrue(info.deviceModel.isNotBlank())
    }
}
