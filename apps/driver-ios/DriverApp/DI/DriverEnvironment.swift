import Foundation

/// Every build-time value the app reads, in one place.
///
/// **This is the only file in the target that may read `Bundle.main`'s Info dictionary.** A build
/// flag read at two call sites is a flag that gets overridden at one of them, and the two that
/// matter here — the gateway origin and the MQTT host — are the pair that decides whether a release
/// build talks to production or to a laptop. Same rule, and the same reason, as
/// `apps/driver-android/.../di/DriverEnvironment.kt` and its `BuildConfig`.
///
/// The values arrive by the iOS equivalent of a `buildConfigField`: `Config/Debug.xcconfig` and
/// `Config/Release.xcconfig` set build settings, `Info.plist` interpolates them into a `MageRide`
/// dictionary, and this reads that dictionary. The reasoning for each value is in the xcconfig.
///
/// Deliberately **plain Foundation** — no `MageRideShared` import. The Kotlin config objects carry
/// `kotlin.time.Duration` fields, which the Objective-C export flattens to raw integers, so they are
/// built on the Kotlin side from these primitives (`IosAppConfig`) rather than here. That keeps the
/// spec's defaults where they are documented instead of re-typed in Swift.
struct DriverEnvironment {

    /// Gateway origin. The app never builds a URL from anything else.
    let apiBaseUrl: String

    /// EMQX host for the position plane (D6' §3).
    let mqttHost: String

    /// 1883 plain in debug, 8883 TLS in release.
    let mqttPort: Int32

    /// Never false outside a local compose stack.
    let mqttTls: Bool

    /// The PMTiles archive the map style reads (D2' §0.1 — R2, no Google Maps).
    let pmTilesUrl: String

    /// `CFBundleShortVersionString`, sent as `X-App-Version` — the D-31 gate's input, not a
    /// diagnostic.
    let appVersion: String

    /// Whether this is a debug build. Decides the API log level, and nothing else.
    let isDebug: Bool

    /// The `User-Agent` this build sends.
    var userAgent: String { "MageRideDriver/\(appVersion) (iOS)" }

    /// The Keychain service name — namespaced per surface so the driver and passenger apps cannot
    /// read each other's session on a handset that has both (AL-08).
    var keychainService: String { "lk.mageride.driver.keychain" }

    /// The values for the running build.
    static let current = DriverEnvironment(
        apiBaseUrl: string(Keys.apiBaseUrl),
        mqttHost: string(Keys.mqttHost),
        mqttPort: Int32(string(Keys.mqttPort)) ?? tlsPort,
        // A plist string, because an xcconfig has no booleans: `MqttTls = YES` is what the
        // Info.plist interpolation produces, and anything other than YES is off. Fail-closed would
        // be wrong here — a build with a typo should not silently talk plaintext to production —
        // so `Release.xcconfig` sets it explicitly and a missing value is a build error there,
        // not a default here.
        mqttTls: string(Keys.mqttTls) == "YES",
        pmTilesUrl: string(Keys.pmTilesUrl),
        appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0",
        isDebug: isDebugBuild
    )

    /// EMQX's TLS listener (`MqttConfig.TLS_PORT`). Duplicated as a literal rather than read off the
    /// Kotlin companion because it is a fallback for a malformed build setting — if the framework
    /// has to be loaded to parse the app's own configuration, a configuration error becomes a
    /// launch crash.
    private static let tlsPort: Int32 = 8883

    // MARK: - Info.plist

    private enum Keys {
        static let root = "MageRide"
        static let apiBaseUrl = "ApiBaseUrl"
        static let mqttHost = "MqttHost"
        static let mqttPort = "MqttPort"
        static let mqttTls = "MqttTls"
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
