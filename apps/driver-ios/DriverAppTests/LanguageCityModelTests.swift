import MageRideShared
import XCTest

@testable import DriverApp

/// SCR-DI-002 — AL-26's Sinhala-first order and AL-27's server-supplied city list.
@MainActor
final class LanguageCityModelTests: XCTestCase {

    /// **AL-26.** The order is fixed by the screen, not by the wire enum, and Sinhala is the
    /// default rather than a translation of English.
    func testTheLanguagesAreSinhalaThenTamilThenEnglishAndSinhalaIsSelected() {
        let model = LanguageCityModel(repository: FakeOnboardingRepository())

        XCTAssertEqual(model.state.languages.map(\.wire), ["si", "ta", "en"])
        XCTAssertEqual(model.state.language.wire, "si")
    }

    /// **AL-27 / US-1.3a.** A hard-coded list would strand a city the Admin Portal had already
    /// opened, so the screen shows what `GET /v1/config/cities` returned and nothing else.
    func testTheCitiesComeFromTheServer() async {
        let repository = FakeOnboardingRepository()
        repository.cityList = [.kandy]
        let model = LanguageCityModel(repository: repository)

        await model.loadCities()

        XCTAssertEqual(model.state.cities.map(\.code), ["kandy"])
        XCTAssertEqual(repository.cityCallCount, 1)
    }

    /// Colombo is pre-selected because `sortOrder` puts it first, not because the app says so —
    /// which is why the assertion is "the first row the server sent".
    func testTheFirstCityIsPreSelected() async {
        let repository = FakeOnboardingRepository()
        let model = LanguageCityModel(repository: repository)

        XCTAssertFalse(model.state.canContinue, "the CTA is dead until a city is chosen")
        await model.loadCities()

        XCTAssertEqual(model.state.cityCode, "colombo")
        XCTAssertTrue(model.state.canContinue)
    }

    /// An empty city picker and an unreachable gateway look identical to a driver, and only one of
    /// them is worth waiting out — so a failure is a Retry, never an empty list.
    func testAFailedCityCallOffersRetryRatherThanAnEmptyList() async {
        let repository = FakeOnboardingRepository()
        repository.citiesFailure = TestFailure()
        let model = LanguageCityModel(repository: repository)

        await model.loadCities()

        XCTAssertTrue(model.state.citiesFailed)
        XCTAssertFalse(model.state.isLoadingCities)
        XCTAssertTrue(model.state.cities.isEmpty)
        XCTAssertFalse(model.state.canContinue)

        repository.citiesFailure = nil
        await model.loadCities()

        XCTAssertFalse(model.state.citiesFailed)
        XCTAssertEqual(model.state.cities.count, 2)
    }

    /// `applyLanguage` is injected rather than called through ``DriverLocale`` directly: the real
    /// one swaps the app bundle's class and writes `AppleLanguages`, and a test that left either
    /// behind would render every later test's strings in Tamil.
    func testConfirmStoresBothAnswersAndAppliesTheLanguage() async {
        let repository = FakeOnboardingRepository()
        var applied: Language?
        let model = LanguageCityModel(repository: repository, applyLanguage: { applied = $0 })
        await model.loadCities()

        model.select(language: Language.ta)
        model.select(cityCode: "kandy")
        XCTAssertTrue(model.confirm())

        XCTAssertEqual(repository.chosen?.language.wire, "ta")
        XCTAssertEqual(repository.chosen?.cityCode, "kandy")
        XCTAssertEqual(applied?.wire, "ta")
    }

    /// The disabled CTA already prevents it; the guard is what stops a keyboard shortcut or a
    /// future caller storing a city nobody picked.
    func testConfirmStoresNothingWithoutACity() {
        let repository = FakeOnboardingRepository()
        var applied: Language?
        let model = LanguageCityModel(repository: repository, applyLanguage: { applied = $0 })

        XCTAssertFalse(model.confirm())
        XCTAssertNil(repository.chosen)
        XCTAssertNil(applied)
    }

    /// A city's Sinhala and Tamil names are **data** — they live in `config.operating_cities` so an
    /// admin can add a city without shipping an app build — so the row renders the server's copy in
    /// the language on screen rather than a string resource.
    func testACityRendersItsOwnNameInTheChosenLanguage() {
        XCTAssertEqual(OperatingCity.colombo.name(language: Language.si), "කොළඹ")
        XCTAssertEqual(OperatingCity.colombo.name(language: Language.ta), "கொழும்பு")
        XCTAssertEqual(OperatingCity.colombo.name(language: Language.en), "Colombo")
    }

    /// An endonym is the same string in all three locales, so it is a constant rather than a key —
    /// three identical values is what `LocalizationTests` reads as a translation nobody did.
    func testTheLanguageBoxesShowEachScriptsOwnName() {
        XCTAssertEqual(LanguageDisplay.endonym(Language.si), "සිංහල")
        XCTAssertEqual(LanguageDisplay.endonym(Language.ta), "தமிழ்")
        XCTAssertEqual(LanguageDisplay.endonym(Language.en), "English")

        XCTAssertEqual(LanguageDisplay.englishName(Language.si), "Sinhala")
        XCTAssertEqual(LanguageDisplay.englishName(Language.ta), "Tamil")
        XCTAssertNil(LanguageDisplay.englishName(Language.en), "the gloss would repeat the endonym")
    }
}
