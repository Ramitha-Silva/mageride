import Foundation

/// Every build-time value the app reads, in one place.
///
/// **This is the only file in the target that may read `Bundle.main`'s Info dictionary.** A build
/// flag read at two call sites is a flag that gets overridden at one of them, and the one that
/// matters here — the gateway origin — is what decides whether a release build talks to production
/// or to a laptop. Same rule, and the same reason, as
/// `apps/passenger-android/.../di/PassengerEnvironment.kt` and its `BuildConfig`.
///
/// The values arrive by the iOS equivalent of a `buildConfigField`: `Config/Debug.xcconfig` and
/// `Config/Release.xcconfig` set build settings, `Info.plist` interpolates them into a `MageRide`
/// dictionary, and this reads that dictionary. The reasoning for each value is in the xcconfig.
///
/// **There is no MQTT host and never will be** (D3' §3.3). Device position *ingest* is MQTT and is
/// the driver's; passenger realtime-*out* is SignalR, and the hub rides ``apiBaseUrl`` — so there is
/// one origin rather than two. `PassengerEnvironmentTests` asserts that no MQTT key exists in the
/// plist, which is the counterpart of the Android module's `ManifestTest`.
///
/// Deliberately **plain Foundation** — no `MageRideShared` import. The Kotlin config objects carry
/// `kotlin.time.Duration` fields, which the Objective-C export flattens to raw integers, so they are
/// built on the Kotlin side from these primitives (`IosAppConfig`) rather than here. That keeps the
/// spec's defaults where they are documented instead of re-typed in Swift.
struct PassengerEnvironment {

    /// Gateway origin. The app never builds a URL from anything else — including the SignalR hub's,
    /// which is this plus `LiveHub.PATH`.
    let apiBaseUrl: String

    /// The PMTiles archive the map style reads (D2' §0.1 — R2, no Google Maps).
    let pmTilesUrl: String

    /// `CFBundleShortVersionString`, sent as `X-App-Version` — the D-31 gate's input, not a
    /// diagnostic.
    let appVersion: String

    /// Whether this is a debug build. Decides the API log level, and nothing else.
    let isDebug: Bool

    /// The `User-Agent` this build sends.
    var userAgent: String { "MageRidePassenger/\(appVersion) (iOS)" }

    /// The Keychain service name — namespaced per surface so the driver and passenger apps cannot
    /// read each other's session on a handset that has both (AL-08). The absence of a shared
    /// keychain access group in the entitlements is the other half.
    var keychainService: String { "lk.mageride.passenger.keychain" }

    /// The values for the running build.
    static let current = PassengerEnvironment(
        apiBaseUrl: string(Keys.apiBaseUrl),
        pmTilesUrl: string(Keys.pmTilesUrl),
        appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0",
        isDebug: isDebugBuild
    )

    // MARK: - Info.plist

    enum Keys {
        static let root = "MageRide"
        static let apiBaseUrl = "ApiBaseUrl"
        static let pmTilesUrl = "PMTilesUrl"
    }

    private static func string(_ key: String) -> String {
        let root = Bundle.main.object(forInfoDictionaryKey: Keys.root) as? [String: Any]
        return root?[key] as? String ?? ""
    }

    private static var isDebugBuild: Bool {
        #if DEBUG
        return true
        #else
        return false
        #endif
    }
}
