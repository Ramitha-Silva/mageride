import Foundation

/// The two MapLibre styles, read out of the bundle and pointed at the deployment's PMTiles archive.
///
/// D2' §0.1 fixes the whole stack: *"Maps are MapLibre GL Native (Android/iOS SDK) over self-served
/// PMTiles on Cloudflare R2 — no Google Maps, no `JBridge`"*, and §0.2 gives the two appearances.
/// The style JSON carries a `__PMTILES_URL__` placeholder rather than a literal because the archive
/// moves between the dev compose stack and R2, and a style is a resource while the URL is a build
/// setting.
///
/// **The JSON is byte-for-byte `apps/driver-android/src/main/res/raw/map_style_{light,dark}.json`.**
/// One cartography for both apps and both platforms: a map that differed between the two would be
/// the most visible divergence this parity fence exists to prevent, and there is nothing
/// platform-specific in a MapLibre style spec document.
///
/// The cartography is deliberately thin — background, earth, landuse, water, road casing and fill,
/// buildings, boundaries and place labels, against the Protomaps basemap's source-layer names. It is
/// a legible map, not a designed one; a full basemap style is a design asset rather than something a
/// shell should invent. See the C067 handoff.
enum MapStyles {

    private static let placeholder = "__PMTILES_URL__"

    /// The style for the appearance in force, written to a temporary file and returned as a URL.
    ///
    /// **MapLibre iOS takes a style URL, not a style string**, so the substituted JSON has to exist
    /// somewhere addressable. The caches directory rather than the bundle: the bundle is read-only
    /// and the substitution depends on a build setting the resource cannot carry. It is rewritten on
    /// every call, which is once per appearance change and costs a few kilobytes.
    static func url(darkMode: Bool, pmTilesUrl: String, bundle: Bundle = MageRideColor.bundle) throws -> URL {
        let name = darkMode ? "MapStyleDark" : "MapStyleLight"
        guard let source = bundle.url(forResource: name, withExtension: "json") else {
            throw MapStyleError.missingResource(name)
        }

        let json = try String(contentsOf: source, encoding: .utf8)
            .replacingOccurrences(of: placeholder, with: pmTilesUrl)

        let destination = FileManager.default
            .urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("\(name).resolved.json")
        try json.write(to: destination, atomically: true, encoding: .utf8)
        return destination
    }

    /// The resolved style as a string, for a caller that wants to inspect it — the tests do.
    static func json(darkMode: Bool, pmTilesUrl: String, bundle: Bundle = MageRideColor.bundle) throws -> String {
        let name = darkMode ? "MapStyleDark" : "MapStyleLight"
        guard let source = bundle.url(forResource: name, withExtension: "json") else {
            throw MapStyleError.missingResource(name)
        }
        return try String(contentsOf: source, encoding: .utf8)
            .replacingOccurrences(of: placeholder, with: pmTilesUrl)
    }
}

enum MapStyleError: Error, Equatable {
    /// A style resource is not in the bundle. A build error dressed as a runtime one.
    case missingResource(String)
}
