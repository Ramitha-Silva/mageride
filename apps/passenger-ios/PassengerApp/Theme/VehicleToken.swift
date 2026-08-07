import SwiftUI

/// MAP-03's vehicle-type legend: a colour and an SF Symbol per canonical type.
///
/// D2' §0.2 fixes this as one table for markers *and* tier cards, so a three-wheeler is the same
/// yellow on the map, in SCR-PA-006's filter, on SCR-PA-007's popup and on SCR-PA-009's tier card.
/// The `Icon` column of that table has two halves — "Compose Material Symbol / SwiftUI SF Symbol" —
/// and this file is the second half; `apps/passenger-android/.../home/VehicleLabels.kt` is the first.
///
/// **This app draws every mode in a cell at once**, which is what makes the whole table load-bearing
/// here in a way it is not on the driver's own-vehicle map: MAP-03's legend is rendered as a
/// MapLibre `match` over all eleven wire values (``VehicleLayers/markerColourExpression()``), so a
/// spelling that does not match `:shared`'s `VehicleType.wire` is a vehicle drawn in the fallback
/// grey rather than a compile error. That defect happened once already — see ``wire``.
///
/// **Train is deliberately distinct**: §0.2 calls out "rail icon, distinct" because a bus and a
/// train sharing a green `bus.fill` is the one confusion Mode A cannot afford — and on this surface
/// Mode A is what a passenger is looking at most of the time.
///
/// The eleven cases are AL-09's canonical set, in the spec's order. `VehicleType` in `:shared` is
/// the wire enum; this maps it to presentation and holds no other opinion.
enum VehicleToken: String, CaseIterable {
    case bus
    case train
    case motorbike
    case threeWheeler
    case flex
    case sedan
    case miniVan
    case van
    case truck
    case miniTruck
    case privateHire

    /// The legend colour. One appearance — see ``MageRideVehicleColor``.
    var color: Color {
        switch self {
        case .bus: return MageRideVehicleColor.bus
        case .train: return MageRideVehicleColor.train
        case .motorbike: return MageRideVehicleColor.motorbike
        case .threeWheeler: return MageRideVehicleColor.threeWheeler
        case .flex: return MageRideVehicleColor.flex
        case .sedan: return MageRideVehicleColor.sedan
        case .miniVan: return MageRideVehicleColor.miniVan
        case .van: return MageRideVehicleColor.van
        case .truck: return MageRideVehicleColor.truck
        case .miniTruck: return MageRideVehicleColor.miniTruck
        case .privateHire: return MageRideVehicleColor.privateHire
        }
    }

    /// The SF Symbol §0.2's table names for this type.
    ///
    /// Every one of these ships in SF Symbols 4 (iOS 16), which is below this app's deployment
    /// target, so none of them can resolve to nothing at runtime. The one row the spec marks with
    /// an asterisk — the three-wheeler, "*custom asset where no SF Symbol exists" — takes
    /// `box.truck.fill` as the table's own fallback until that asset is drawn; it is the closest
    /// silhouette Apple ships and it is what the spec puts in the cell.
    var symbolName: String {
        switch self {
        case .bus: return "bus.fill"
        case .train: return "tram.fill"
        case .motorbike: return "bicycle"
        case .threeWheeler: return "box.truck.fill"
        case .flex, .sedan: return "car.fill"
        case .miniVan, .van: return "van.fill"
        case .truck, .miniTruck: return "truck.box.fill"
        case .privateHire: return "shippingbox.fill"
        }
    }

    /// The symbol as a view, tinted with the legend colour.
    var image: Image { Image(systemName: symbolName) }

    /// The canonical wire spelling in `:shared`'s `VehicleType` (AL-09).
    ///
    /// Kept as a property rather than a `VehicleType` extension so this file compiles and is
    /// testable without the KMP framework — the presentation table is the thing under test.
    ///
    /// **These were camel case on the driver side until C088 and that was a defect**, recorded in
    /// the C087 handoff. They are snake case from the start here. `VehicleType.wire` in
    /// `:shared` is `registry.vehicles.vehicle_type`'s own value and is **snake case**
    /// (`three_wheeler`, `mini_van`, `mini_truck`); camel case made ``forWire(_:)`` answer `nil` for
    /// three of the ten types and made ``VehicleLayers/markerColourExpression()`` build a match stop
    /// on `"threewheeler"` against a GeoJSON attribute carrying `"three_wheeler"` — a three-wheeler,
    /// a mini van and a mini truck all drawn in the fallback grey. `MapAndVehicleTokenTests` pins
    /// every one of the eleven against the wire spelling for that reason.
    ///
    /// `private` has no `VehicleType` counterpart: it is §0.2's eleventh legend row and the
    /// fallback colour, not a type the platform stores.
    var wire: String {
        switch self {
        case .bus: return "bus"
        case .train: return "train"
        case .motorbike: return "motorbike"
        case .threeWheeler: return "three_wheeler"
        case .flex: return "flex"
        case .sedan: return "sedan"
        case .miniVan: return "mini_van"
        case .van: return "van"
        case .truck: return "truck"
        case .miniTruck: return "mini_truck"
        case .privateHire: return "private"
        }
    }

    /// The token for a wire value, or `nil` for one this build does not know.
    ///
    /// `nil` rather than a default: an unknown vehicle type drawn as a sedan is a map that lies,
    /// and a server that adds a type should show up as a missing marker rather than a wrong one.
    static func forWire(_ wire: String) -> VehicleToken? {
        allCases.first { $0.wire.caseInsensitiveCompare(wire) == .orderedSame }
    }
}

/// D2' §0.2's mode badges: Mode A green, Mode B grey, Mode C orange.
enum ModeToken: String, CaseIterable {
    case a
    case b
    case c

    var color: Color {
        switch self {
        case .a: return MageRideVehicleColor.modeA
        case .b: return MageRideVehicleColor.modeB
        case .c: return MageRideVehicleColor.modeC
        }
    }
}
