import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

/// SCR-PI-008 — the destination field.
///
/// **AL-17: geo only.** The interesting assertions here are the negative ones — that typing `138`
/// reaches the geocoder and nothing else, and that no route row can appear in the list. The fence is
/// structural as well as asserted: ``PassengerPlaces`` has no route lookup on it at all, so
/// `QueryApi.getBusesOnRoute` is unreachable from this screen without adding a method to a protocol.
///
/// D2' §SCR-*-008 says the opposite and needs a micro-change-set; the wireframe is the approved
/// baseline and AL-17 is the later decision. See ``SearchLocationModel`` and the C096 handoff.
final class SearchLocationModelTests: XCTestCase {

    private var places: FakePassengerPlaces!
    private var recents: FakeRecentPlaces!

    @MainActor
    override func setUp() {
        super.setUp()
        places = FakePassengerPlaces()
        places.saved = [HomeFixtures.home]
        recents = FakeRecentPlaces([HomeFixtures.nugegoda])
    }

    /// The fence, asserted from both ends. `138` is a real Colombo bus route and is exactly what D2'
    /// §SCR-*-008 says this field accepts; the digits go to the **geocoder**, and what comes back is
    /// places.
    @MainActor
    func testTypingARouteNumberReturnsPlacesAndNeverARoute() async {
        places.searchResults = [HomeFixtures.fort, HomeFixtures.pettah]
        let model = await loadedModel()

        model.onQueryChanged("138")
        // Waits for the GEOCODER, not for a count. `loadedModel()` has already put two saved places
        // in `predictions`, so `count == 2` was true before the debounce had even elapsed and every
        // assertion below read the defaults instead of the search.
        await eventually("the lookup landed") { await MainActor.run { self.places.searches.last?.text } == "138" }

        XCTAssertEqual(places.searches.last?.text, "138", "the digits went to the geocoder")
        XCTAssertEqual(
            model.state.predictions.map(\.displayName),
            [HomeFixtures.fort.displayName, HomeFixtures.pettah.displayName]
        )
        // Every prediction resolves to a coordinate, which is what "a destination is a place"
        // means. A route row would have neither.
        XCTAssertTrue(model.state.predictions.allSatisfy { $0.lat != 0 && $0.lng != 0 })
    }

    /// §2.2's `place_recents` is written **here**, not by a booking: the table is *"recent /
    /// searched locations"* and searching is what happens on this screen, so a passenger who looked
    /// somewhere up and then changed their mind still gets the row.
    @MainActor
    func testChoosingAPredictionIsWhatWritesTheRecent() async {
        places.searchResults = [HomeFixtures.fort]
        let model = await loadedModel()
        model.onQueryChanged("Fort")
        await eventually("the lookup landed") { await MainActor.run { !model.state.predictions.isEmpty } }

        model.choose(HomeFixtures.fort)

        await eventually("the row was written") { await MainActor.run { self.recents.rows.count } == 2 }
        XCTAssertEqual(recents.rows.first?.displayName, HomeFixtures.fort.displayName)
    }

    /// Two characters is nothing to a geocoder and everything to a rate limit — Nominatim is
    /// self-hosted (D-14) but shared with every passenger in the country.
    @MainActor
    func testAQueryShorterThanThreeCharactersShowsSavedPlacesInstead() async {
        let model = await loadedModel()

        model.onQueryChanged("Fo")
        try? await Task.sleep(nanoseconds: 400_000_000)

        XCTAssertTrue(model.state.showingDefaults)
        XCTAssertTrue(places.searches.isEmpty, "nothing went out for two characters")
        // The wireframe's *"Empty → recents/saved"* — literally both, in that order.
        XCTAssertEqual(
            model.state.predictions.map(\.displayName),
            [HomeFixtures.nugegoda.displayName, HomeFixtures.home.label]
        )
        XCTAssertEqual(model.state.predictions.first?.source, GeocodedPlaceSource.recent, "🕘")
        XCTAssertEqual(model.state.predictions.last?.source, GeocodedPlaceSource.saved, "★")
    }

    /// Debounced, and the answer that lands is the one for what is on screen — not for the third
    /// letter of it.
    @MainActor
    func testTypingQuicklyMakesOneRequestRatherThanOnePerLetter() async {
        places.searchResults = [HomeFixtures.fort]
        let model = await loadedModel()

        for text in ["For", "Fort", "Fort ", "Fort R"] {
            model.onQueryChanged(text)
        }
        await eventually("one lookup landed") { await MainActor.run { self.places.searches.count } == 1 }
        try? await Task.sleep(nanoseconds: 400_000_000)

        XCTAssertEqual(places.searches.count, 1)
        XCTAssertEqual(places.searches.last?.text, "Fort R", "and it is the last thing typed")
    }

    /// The lookup is ranked around the passenger when the screen was given a position, and simply is
    /// not when it was not — see ``HomeDestinationView`` for why it is `nil` today.
    @MainActor
    func testTheLookupIsBiasedTowardsWhereThePassengerIs() async {
        places.searchResults = []
        let model = await loadedModel(around: HomeFixtures.colombo)

        model.onQueryChanged("Fort")
        await eventually("the lookup went out") { await MainActor.run { !self.places.searches.isEmpty } }

        XCTAssertEqual(places.searches.last?.around?.lat, HomeFixtures.colombo.lat)
        XCTAssertEqual(places.searches.last?.around?.lng, HomeFixtures.colombo.lng)
    }

    /// The wireframe's *"geocoder down → Pick on map"*. Not an error dialog: the passenger still has
    /// a map and a pin, which is a better answer than a retry button.
    @MainActor
    func testAGeocoderFailureOffersTheMapRatherThanAnErrorDialog() async {
        places.searchFailure = HomeFakeError.unreachable
        let model = await loadedModel()

        model.onQueryChanged("Fort")
        await eventually("the failure landed") { await MainActor.run { model.state.geocoderDown } }

        XCTAssertFalse(model.state.isSearching)
    }

    @MainActor
    func testANewKeystrokeClearsThePreviousFailure() async {
        places.searchFailure = HomeFakeError.unreachable
        let model = await loadedModel()
        model.onQueryChanged("Fort")
        await eventually("the failure landed") { await MainActor.run { model.state.geocoderDown } }

        places.searchFailure = nil
        places.searchResults = [HomeFixtures.fort]
        model.onQueryChanged("Fort R")
        await eventually("the lookup landed") { await MainActor.run { !model.state.predictions.isEmpty } }

        XCTAssertFalse(model.state.geocoderDown, "the field works again and stops saying it does not")
    }

    /// iam is unreachable; the local recents are still the passenger's own and still work. §2.2 is a
    /// read of the handset, not of the platform, which is the whole reason it is a table.
    @MainActor
    func testTheEmptyStateSurvivesAnUnreachableAddressBook() async {
        places.savedFailure = HomeFakeError.unreachable
        let model = await loadedModel()

        XCTAssertEqual(model.state.predictions.map(\.displayName), [HomeFixtures.nugegoda.displayName])
    }

    // MARK: -

    @MainActor
    private func loadedModel(around: GeoPoint? = nil) async -> SearchLocationModel {
        let model = SearchLocationModel(places: places, recents: recents, around: around)
        await model.load()
        return model
    }
}
