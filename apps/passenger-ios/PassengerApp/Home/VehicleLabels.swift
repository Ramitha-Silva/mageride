import MageRideShared
import SwiftUI

/// D2' §0.2's vehicle table as a *screen* needs it, and the bridge from `:shared`'s wire enums onto
/// this app's presentation tokens.
///
/// **The colour and the glyph are already ``VehicleToken``'s** — that type is §0.2's legend and is
/// what ``VehicleLayers`` tints a marker with, so a second copy of either here would be exactly the
/// duplication MAP-03 exists to prevent. What is missing from it, and what this file adds, is the
/// two things a card needs and a marker does not: a **trilingual display name**, and a way to get
/// from a `VehicleType` / `ServiceMode` off the wire to the token in the first place.
///
/// The split is why ``VehicleToken`` stays free of `MageRideShared`: it is read by
/// `MapAndVehicleTokenTests` and by the layer builders, neither of which should need the framework
/// to know what colour a three-wheeler is. `apps/passenger-android/.../home/VehicleLabels.kt` is one
/// object because Compose has no equivalent separation.
enum VehicleLabels {

    /// The eight chips `passenger_ios.html` draws under *"vehicle types"*, **in its order**.
    ///
    /// Not all eleven legend rows, and the three that are missing are missing for a reason:
    /// `truck` and `mini_truck` are delivery-only (AL-09, Epic 20) and a passenger does not hail
    /// one, and `private` is the Mode B grey — already covered by the Mode B toggle one row above,
    /// so a chip for it would be a second control over the same vehicles.
    ///
    /// The wireframe itself draws seven (`Bus · Train · Tuk · Flex · Sedan · Mini Van · Van`) and
    /// stops at the edge of a 320pt mock; **Motorbike is the eighth**, because AL-09 makes it a
    /// hailable type and a chip row that wrapped would have shown it. The Android twin draws the
    /// same eight.
    ///
    /// **Train is its own chip**, which is the whole of US-7.7's *"trains separate"* — it is a
    /// distinct type with a distinct colour (§0.2 calls the rail red out explicitly), so filtering
    /// it needs no special case.
    static let chipTypes: [VehicleToken] = [
        .bus,
        .train,
        .threeWheeler,
        .flex,
        .sedan,
        .miniVan,
        .van,
        .motorbike,
    ]

    /// The three mode rows, in the wireframe's order.
    static let modeRows: [ModeToken] = [.a, .b, .c]
}

extension VehicleToken {

    /// The token for a wire enum, or `nil` when the frame carried no type.
    ///
    /// Goes through ``VehicleToken/forWire(_:)`` rather than a second `switch`, so the one place a
    /// spelling can be wrong stays the one place `MapAndVehicleTokenTests` already checks.
    static func forType(_ type: VehicleType?) -> VehicleToken? {
        guard let type else { return nil }
        return forWire(type.wire)
    }

    /// The trilingual display name's key.
    ///
    /// Written as ten literals rather than `"vehicle_" + wire`, and that is not verbosity:
    /// `LocalizationTests` finds a key by searching the sources for its quoted spelling, so a
    /// composed key is a key it reports as declared-but-never-referenced.
    var nameKey: String {
        switch self {
        case .bus: return "vehicle_bus"
        case .train: return "vehicle_train"
        case .motorbike: return "vehicle_motorbike"
        case .threeWheeler: return "vehicle_three_wheeler"
        case .flex: return "vehicle_flex"
        case .sedan: return "vehicle_sedan"
        case .miniVan: return "vehicle_mini_van"
        case .van: return "vehicle_van"
        case .truck: return "vehicle_truck"
        case .miniTruck: return "vehicle_mini_truck"
        // §0.2's eleventh legend row is the *fallback* grey rather than a type the platform stores,
        // so it reads as the same "we were not told what this is" the absent case does.
        case .privateHire: return VehicleLabels.unknownTypeKey
        }
    }
}

extension VehicleLabels {

    /// What a vehicle with no type is called. Also ``VehicleToken/privateHire``'s name — see there.
    static let unknownTypeKey = "vehicle_unknown"
}

extension ModeToken {

    /// The token for `:shared`'s mode, or `nil` when the frame carried none.
    ///
    /// Compared against the three singletons rather than read off `KotlinEnum.name`: an exported
    /// enum entry is one object, `==` is `isEqual:` over it, and this is the idiom the driver app
    /// already uses everywhere it branches on a mode.
    static func forMode(_ mode: ServiceMode?) -> ModeToken? {
        guard let mode else { return nil }
        if mode == ServiceMode.a { return .a }
        if mode == ServiceMode.b { return .b }
        if mode == ServiceMode.c { return .c }
        return nil
    }

    /// The wireframe's `A` / `B` / `C` badge letter.
    ///
    /// A Swift constant rather than three `.strings` entries, because the letter is the same
    /// character in all three scripts — three identical values is precisely what
    /// `LocalizationTests` reads as a translation nobody did.
    var badge: String { rawValue.uppercased() }

    /// The row label's key — the wireframe's *"Mode A — Bus & Train"*.
    var nameKey: String {
        switch self {
        case .a: return "mode_a"
        case .b: return "mode_b"
        case .c: return "mode_c"
        }
    }
}
