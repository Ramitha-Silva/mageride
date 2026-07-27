package lk.mageride.shared.platform

import platform.UIKit.UIDevice

/** Compiled and verified on macOS only — this host declares the target but cannot link it. */
public actual fun platformInfo(): PlatformInfo = with(UIDevice.currentDevice) {
    PlatformInfo(
        os = "iOS",
        osVersion = systemVersion,
        deviceModel = model,
    )
}
