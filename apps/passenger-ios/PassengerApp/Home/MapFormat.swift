import Foundation
import MageRideShared

/// The two numbers cluster 2 prints: a straight-line distance and an ETA.
///
/// Both are trilingual formats rather than interpolations — `%1$d m`, `%1$d.%2$d km` and `%1$d min`
/// are the Android twin's own keys, so the three `.strings` files and the three `strings.xml` files
/// carry the same spellings and a divergence between the two apps is a diff.
enum MapFormat {

    /// Straight-line metres or kilometres between two points, or the *unknown* string when the
    /// passenger's own position is not known yet.
    ///
    /// `:shared`'s `distanceMetres` is the haversine C017 uses for every exact-distance rule on the
    /// platform, so the number here is the one dispatch would compute — which is the point of having
    /// one implementation. It is a **straight line**, it is labelled as a distance, and it is never
    /// converted into a time.
    static func distance(from origin: MapFix?, to point: GeoPoint) -> String {
        guard let origin else { return "popup_distance_unavailable".localised }
        let metres = GeoDistanceKt.distanceMetres(from: GeoPoint(lat: origin.lat, lng: origin.lng), to: point)
        return distance(metres: metres)
    }

    /// `350 m` under a kilometre, `2.4 km` above it — the wireframe's own two forms.
    static func distance(metres: Double) -> String {
        let rounded = Int(metres.rounded())
        guard rounded >= metresInKilometre else {
            return "popup_distance_m".localisedFormat(rounded)
        }
        return "popup_distance_km".localisedFormat(rounded / metresInKilometre, (rounded % metresInKilometre) / 100)
    }

    /// Minutes to the vehicle, **rounded up**.
    ///
    /// Up rather than to-nearest: a bus that is 89 seconds away is *"2 min"*, and a passenger who
    /// reads *"1 min"* and misses it by twenty seconds was told the wrong thing. Under a minute is
    /// its own string — *"0 min"* is not an arrival. `nil` is the lookup that has not landed, or the
    /// one that failed; it says so rather than inventing a number.
    static func eta(seconds: Int?) -> String {
        guard let seconds else { return "popup_eta_unavailable".localised }
        guard seconds >= secondsInMinute else { return "popup_eta_now".localised }
        return "popup_eta_minutes".localisedFormat((seconds + secondsInMinute - 1) / secondsInMinute)
    }

    private static let metresInKilometre = 1_000
    private static let secondsInMinute = 60
}

/// Punctuation the app draws, which is not copy.
///
/// The `·` between a plate and a driver's name, or between an address line and its city, is the same
/// character in Sinhala, Tamil and English — three identical values in the three `.strings` files is
/// exactly what `LocalizationTests` reads as a translation nobody did. `apps/driver-ios` carries the
/// same constant for the same reason.
enum MageRideSymbols {
    static let separator = " · "

    /// The wireframe's `Nugegoda → Galle Face` (Δ C099). Same value as `apps/driver-ios`'s.
    static let routeArrow = " → "

    /// *"we have not been told"* — a read in flight, or a value the contract has no operation for.
    /// The same character ``MoneyFormat/pending`` uses, and for the same reason.
    static let unknown = "—"

    /// What SCR-PI-023's Date row puts between `17 Jun` and `08:32`.
    static let dateTimeSeparator = ", "
}
