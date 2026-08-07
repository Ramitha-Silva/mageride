import Foundation

/// The four ways a location can be captured (AL-20's addition included).
///
/// The **wireframe** draws four on SCR-PI-010b — Search, Map pin, Paste link, Request — and this
/// component's fence repeats them. D2' §SCR-PA-010b's own component table still says three; it
/// predates AL-20, and D2' §SCR-*-012a already names *"010b proxy pickup"* as a paste-link entry
/// point, so the spec disagrees with itself and the wireframe settles it. Recorded in the C079
/// handoff and restated in C097's.
enum PickupMethod: String, CaseIterable, Identifiable {
    case search
    case map
    case pasteLink
    case request

    var id: String { rawValue }

    /// The wireframe's own labels for the four.
    var labelKey: String {
        switch self {
        case .search: return "capture_search"
        case .map: return "capture_map"
        case .pasteLink: return "capture_paste"
        case .request: return "capture_request"
        }
    }
}

/// Which end of a parcel's journey a capture control is filling in.
///
/// The two are **not** symmetric, and the fence says so: pickup offers Search / Map / Paste link,
/// drop-off offers those plus **Request**. That is not an oversight — the sender is standing at the
/// pickup, so there is nobody to ask; the recipient is the only person who knows where the parcel is
/// going, and asking them is what the fourth method is for.
enum PackageEnd {
    case pickup
    case dropoff

    /// Which methods this end offers, in the wireframe's order.
    var methods: [PickupMethod] {
        switch self {
        case .pickup: return PickupMethod.packagePickupMethods
        case .dropoff: return PickupMethod.allMethods
        }
    }
}

extension PickupMethod {

    /// SCR-PI-012's pickup: three methods. There is nobody standing there to ask (see ``PackageEnd``).
    static let packagePickupMethods: [PickupMethod] = [.search, .map, .pasteLink]

    /// SCR-PI-012's drop-off, and SCR-PI-010b's pickup: all four.
    static let allMethods: [PickupMethod] = PickupMethod.allCases
}
