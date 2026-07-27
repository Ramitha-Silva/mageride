package lk.mageride.shared.platform

/**
 * Who the client is, from the shared layer's point of view.
 *
 * This is one of the few things `commonMain` genuinely cannot compute: the gateway's
 * attestation and version gate (C008) key off the OS and the app build, and only the
 * platform knows them. Everything else in this module stays platform-agnostic —
 * see `shared/kmp/CLAUDE.md` for the expect/actual rule.
 *
 * @property os          `"Android"` or `"iOS"`.
 * @property osVersion   Release string as the OS reports it, e.g. `"14"` / `"17.5"`.
 * @property deviceModel Manufacturer + model, best effort; never blank.
 */
public data class PlatformInfo(val os: String, val osVersion: String, val deviceModel: String) {
    /** Value for the `X-Client-Platform` request header (D3' §0). */
    public val clientPlatform: String get() = os.lowercase()
}

/** The running platform. Implemented in `androidMain` and `iosMain`. */
public expect fun platformInfo(): PlatformInfo
