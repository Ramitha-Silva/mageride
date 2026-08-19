import Combine
import Foundation
import MageRideShared

/// ``MapPickSheet``'s state — a search box and a pin, over one map.
struct MapPickState {

    /// What is in the search field.
    var query: String = ""

    /// What the geocoder answered. Drawn **over** the map rather than above it, so the sheet keeps
    /// its height and the CTA never leaves the screen.
    var predictions: [GeocodedPlace] = []

    /// A lookup is in flight.
    var searching = false

    /// The lookup failed. Not an error state for the sheet: the pin still works, which is the whole
    /// reason a map picker exists — AL-14 makes the same call about a reverse geocode being a
    /// pre-fill and never a gate.
    var geocoderDown = false

    /// Where the pin is: the map's centre, reported by `onCameraIdle`.
    var centre: GeoPoint?

    /// The searched place the pin is currently sitting on, or `nil` for a pin the passenger placed
    /// by hand. This is what gives the committed ``Place`` a **name**.
    var chosen: GeocodedPlace?

    /// A camera move the sheet is asking for. ``MageRideMap`` applies it and the passenger sees the
    /// map fly to their search result.
    var focus: GeoPoint?

    /// What *"Use this location"* commits.
    ///
    /// **The coordinates are always the pin's and never the search result's.** They are the same
    /// point until the passenger nudges the map, and after that the pin is what they are looking at
    /// — a picker that committed the place they searched for rather than the spot they aimed at
    /// would move the pickup after they had placed it. The NAME comes from ``chosen``, which
    /// ``MapPickModel/onPinMoved(_:)`` drops as soon as the pin leaves it.
    var selection: Place? {
        centre.map { Place(lat: $0.lat, lng: $0.lng, address: chosen?.displayName) }
    }

    /// Whether the field is too short to spend a geocoder request on.
    var showingDefaults: Bool {
        query.trimmingCharacters(in: .whitespaces).count < MapPickModel.minimumQueryLength
    }
}

/// The **Map** capture method, with a search box in it.
///
/// **Why a search here at all, when SCR-PI-008 is one tap away on the same row.** The two answer
/// different questions. SCR-PI-008 turns a *name* into a coordinate and is done; this sheet is for a
/// pickup that has no name a geocoder knows — a lane, a gate, the third house past the junction —
/// and searching is how a passenger gets the map *near* it before placing the pin by eye. Landing on
/// the right junction and then dragging fifty metres is the gesture; without the search the only way
/// to reach a junction across town is to pan there.
///
/// **A search result is a camera move, not a commitment.** Tapping a prediction moves the pin there
/// and names it; the passenger can then nudge the map, and the moment the pin leaves the named place
/// the name is dropped and the label falls back to the coordinates. What is committed is always
/// where the pin is — see ``MapPickState/selection``.
///
/// Geo only, debounced, and biased toward the pin: the same three rules ``SearchLocationModel``
/// keeps, for the same reasons (AL-17, and a self-hosted Nominatim shared with the whole country).
@MainActor
final class MapPickModel: ObservableObject {

    @Published private(set) var state = MapPickState()

    private let places: PassengerPlaces
    private var lookup: Task<Void, Never>?

    init(places: PassengerPlaces) {
        self.places = places
    }

    deinit {
        lookup?.cancel()
    }

    /// The sheet was opened over `around`.
    ///
    /// Called on every appearance rather than only on construction, because one sheet serves
    /// SCR-PI-010b's pickup and both of SCR-PI-012's ends — a query left in the field from the last
    /// thing the passenger captured would be somebody else's search.
    func opened(around: GeoPoint?) {
        lookup?.cancel()
        state = MapPickState(centre: around)
    }

    /// A keystroke in the search field. Debounced; the previous lookup is cancelled.
    func onQueryChanged(_ input: String) {
        state.query = input
        state.geocoderDown = false
        lookup?.cancel()

        guard !state.showingDefaults else {
            state.predictions = []
            state.searching = false
            return
        }

        let text = input.trimmingCharacters(in: .whitespaces)
        lookup = Task { [weak self] in
            try? await Task.sleep(nanoseconds: Self.debounceNanoseconds)
            guard !Task.isCancelled else { return }
            await self?.search(text)
        }
    }

    /// A prediction was tapped: fly the pin to it and remember what it is called.
    ///
    /// The list closes with it. It is drawn over the map, and a passenger who has just chosen where
    /// to look wants to see the place rather than the list they left behind.
    func onPredictionChosen(_ place: GeocodedPlace) {
        let point = GeoPoint(lat: place.lat, lng: place.lng)
        state.predictions = []
        state.chosen = place
        state.focus = point
        state.centre = point
    }

    /// The map settled somewhere.
    ///
    /// **This is where a searched name stops being true.** The camera lands on the result within a
    /// metre or so of what was asked for, so a small tolerance keeps the name through the settle
    /// itself; a genuine pan past it means the pin is no longer on that place and the label goes
    /// back to the coordinates. Clearing ``MapPickState/focus`` with it is what lets the same
    /// prediction be tapped a second time and move the map again.
    func onPinMoved(_ point: GeoPoint) {
        let stillOnChosen = state.chosen.map { chosen in
            GeoDistanceKt.distanceMetres(
                from: GeoPoint(lat: chosen.lat, lng: chosen.lng),
                to: point
            ) <= Self.settleToleranceMetres
        } ?? false

        state.centre = point
        if !stillOnChosen {
            state.chosen = nil
            state.focus = nil
        }
    }

    private func search(_ text: String) async {
        state.searching = true
        do {
            let found = try await places.search(
                text,
                around: state.centre,
                limit: Self.resultLimit
            )
            guard !Task.isCancelled else { return }
            state.predictions = found
            state.searching = false
        } catch {
            // The pin is unaffected by a geocoder that cannot answer, so this is a line of text
            // rather than a state: a passenger with no geocoder can still place a pickup, which is
            // what this sheet is for.
            guard !Task.isCancelled else { return }
            state.predictions = []
            state.searching = false
            state.geocoderDown = true
        }
    }

    /// The same floor SCR-PI-008 keeps, and for the same reason — see ``SearchLocationModel``.
    static let minimumQueryLength = 3

    /// SCR-PI-008's number: 300 ms is the usual floor for "stopped typing" without feeling laggy.
    static let debounceNanoseconds: UInt64 = 300_000_000

    /// A short list: it is drawn over a map the passenger is trying to look at.
    static let resultLimit = 5

    /// How far the pin may sit from a searched place and still wear its name.
    ///
    /// Twenty-five metres is wider than the camera's own settling error and narrower than any two
    /// addresses — a pin this close is on the place, and a pin further away is not.
    static let settleToleranceMetres: Double = 25
}
