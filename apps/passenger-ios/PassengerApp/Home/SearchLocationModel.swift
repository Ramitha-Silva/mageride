import Foundation
import MageRideShared

/// SCR-PI-008's state.
struct SearchLocationState {

    /// What is in the drop field.
    var query = ""

    /// Geocoded places and the passenger's own saved/recent ones. **Never a route** — see the
    /// model's note.
    var predictions: [GeocodedPlace] = []

    /// A lookup is in flight.
    var isSearching = false

    /// The lookup failed. The wireframe's *"Pick on map"* is the way out.
    var geocoderDown = false

    /// Whether the field is short enough that the screen shows saved/recent instead.
    var showingDefaults: Bool {
        query.trimmingCharacters(in: .whitespacesAndNewlines).count < Self.minimumQueryLength
    }

    /// How much has to be typed before a lookup goes out.
    ///
    /// Two characters is nothing to a geocoder and everything to a rate limit: Nominatim is
    /// self-hosted (D-14) but shared with every other passenger in the country, and a request per
    /// keystroke from the first one turns a search box into a load test.
    static let minimumQueryLength = 3
}

/// SCR-PI-008 — the destination field.
///
/// **GEO ONLY. A route number is not a destination (AL-17).** Typing `138` returns places called
/// "138" or nothing; it never returns a route row, and selecting a prediction always yields a
/// coordinate. This is a **fence**, and it is the one place in this app where a specification
/// disagrees with itself:
///
/// - `D2' §SCR-PA-008` says the drop field *"accepts a destination place **or** a public route
///   number (e.g. `138`)"* and that predictions *"blend matched public routes (bus/train) with
///   Nominatim/Photon geocoded places"*, and sketches a route row.
/// - **AL-17, the `passenger_ios.html` cell and this component's prompt all say the opposite** — the
///   cell's own state line reads *"route numbers **not** accepted … no route rows"*.
///
/// The wireframe is the approved baseline and AL-17 is the later decision, so they win; D2'
/// §SCR-*-008 needs a micro-change-set. C078 recorded the same conflict from the Android side, along
/// with what it costs US-7.9 — which has no screen on either platform.
///
/// The fence is also structural: ``PassengerPlaces`` has no route lookup on it at all, so
/// `QueryApi.getBusesOnRoute` is unreachable from this screen without adding a method.
///
/// **Once something is typed, the blend is the server's.** `GET /v1/geo/search` answers
/// `GeocodedPlace.source` of `nominatim`, `saved` or `recent`, so a query is one call rather than
/// three lists merged on the device — and the ★ row the wireframe draws among the 📍 ones is that
/// field, not a separate section. The **empty** state is the exception and has to be: there is no
/// "search for nothing" request, so it is assembled here from §2.2's local recents and the saved
/// addresses.
@MainActor
final class SearchLocationModel: ObservableObject {

    @Published private(set) var state = SearchLocationState()

    private let places: PassengerPlaces
    private let recents: RecentPlaces

    /// Where to bias the geocoder — the passenger's own position, handed over by the map.
    private var around: GeoPoint?

    private var lookup: Task<Void, Never>?
    private var hasLoadedDefaults = false

    init(places: PassengerPlaces, recents: RecentPlaces, around: GeoPoint?) {
        self.places = places
        self.recents = recents
        self.around = around
    }

    deinit {
        lookup?.cancel()
    }

    /// The empty state. Idempotent — `.task` may run again after a scene change.
    func load() async {
        guard !hasLoadedDefaults else { return }
        hasLoadedDefaults = true
        await loadDefaults()
    }

    /// A keystroke.
    ///
    /// Debounced rather than fired per character — see ``SearchLocationState/minimumQueryLength``.
    /// The previous lookup is cancelled, so a passenger typing quickly makes one request rather than
    /// one per letter, and the answer that lands is always the one for what is on screen.
    func onQueryChanged(_ input: String) {
        state.query = input
        state.geocoderDown = false
        lookup?.cancel()

        guard !state.showingDefaults else {
            lookup = Task { await loadDefaults() }
            return
        }

        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        lookup = Task { [weak self] in
            try? await Task.sleep(nanoseconds: Self.debounceNanoseconds)
            guard !Task.isCancelled else { return }
            await self?.search(text)
        }
    }

    /// A prediction was chosen.
    ///
    /// Writes §2.2's `place_recents` row, which is what puts the place in SCR-PI-010's *"Recent"*
    /// list — this screen is the only writer, because *"recent / searched locations"* is what the
    /// table is for and searching is what happens here. **Local-only**: nothing is sent anywhere.
    ///
    /// Fire-and-forget, and deliberately not awaited by the caller: navigating on is the passenger's
    /// answer to their own tap, and it must not wait on a protected-file write. A database that will
    /// not open costs the row, not the destination — see ``LocalRecentPlaces``.
    func choose(_ place: GeocodedPlace) {
        Task { [recents] in await recents.remember(place) }
    }

    // MARK: -

    private func search(_ text: String) async {
        state.isSearching = true
        do {
            let found = try await places.search(text, around: around, limit: Self.resultLimit)
            guard !Task.isCancelled else { return }
            state.predictions = found
            state.isSearching = false
        } catch {
            guard !Task.isCancelled else { return }
            // The wireframe's *"geocoder down → Pick on map"*. Not an error dialog: the passenger
            // still has a map and a pin, which is a better answer than a retry button.
            state.isSearching = false
            state.geocoderDown = true
        }
    }

    /// The empty state — the wireframe's *"recents/saved"*, literally both and in that order.
    ///
    /// The recents are §2.2's local table (this screen writes it, see ``choose(_:)``) and the saved
    /// addresses are `iam.users`; a recent that is also saved would be two rows for one place, so the
    /// saved ones are filtered against what the recents already list.
    private func loadDefaults() async {
        let recent = await recents.recent()
        guard !Task.isCancelled else { return }

        guard let saved = try? await places.savedAddresses() else {
            // iam is unreachable; the local recents are still the passenger's own and still work.
            state.predictions = recent
            state.isSearching = false
            return
        }
        guard !Task.isCancelled else { return }

        let seen = Set(recent.map(\.displayName))
        state.predictions = recent + saved.map(\.asPlace).filter { !seen.contains($0.displayName) }
        state.isSearching = false
    }

    /// How long the field rests before a lookup goes out.
    ///
    /// The wireframe says *"debounced"* and pins no number. 300 ms is the usual floor for "stopped
    /// typing" without feeling laggy; anything shorter sends a request mid-word. Same value as the
    /// Android twin's.
    private static let debounceNanoseconds: UInt64 = 300_000_000

    /// Enough rows to fill the list without scrolling into a second screen of guesses.
    private static let resultLimit = 8
}
