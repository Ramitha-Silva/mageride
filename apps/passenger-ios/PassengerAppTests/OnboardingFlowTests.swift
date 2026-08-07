import MageRideShared
import XCTest

@testable import PassengerApp

/// The first-run gate, and the two screens that feed it.
///
/// ``OnboardingRouter`` is the one piece of C095 that decides what a passenger sees on every cold
/// start, it has to agree with what SCR-PI-002, SCR-PI-003 and SCR-PI-004 each do when they finish,
/// and none of that is worth discovering on a handset — which is why it is a pure function and why
/// this is the first suite in the file.
final class OnboardingRouterTests: XCTestCase {

    /// **The language is asked first**, before the session. A passenger who has not chosen one would
    /// otherwise meet the login screen in whatever locale the handset happens to be set to, which
    /// for most users here is not one of the three (AL-26).
    func testAFirstRunGoesToOnboardingWhateverElseIsTrue() {
        for signedIn in [true, false] {
            for profile in [true, false] {
                XCTAssertEqual(
                    OnboardingRouter.next(
                        signedIn: signedIn,
                        firstRunComplete: false,
                        profileComplete: profile,
                        locationAcknowledged: true
                    ),
                    .onboarding
                )
            }
        }
    }

    func testTheGateRunsInOrder() {
        XCTAssertEqual(
            OnboardingRouter.next(signedIn: false, firstRunComplete: true, profileComplete: false, locationAcknowledged: false),
            .login
        )
        XCTAssertEqual(
            OnboardingRouter.next(signedIn: true, firstRunComplete: true, profileComplete: false, locationAcknowledged: false),
            .profileSetup
        )
        XCTAssertEqual(
            OnboardingRouter.next(signedIn: true, firstRunComplete: true, profileComplete: true, locationAcknowledged: false),
            .locationPermission
        )
        XCTAssertEqual(
            OnboardingRouter.next(signedIn: true, firstRunComplete: true, profileComplete: true, locationAcknowledged: true),
            .liveMap
        )
    }

    /// Every outcome resolves to a route the shell registers — ``PassengerRoute`` is the only place
    /// a path is spelt, and a destination pointing at an unregistered one would be a dead end.
    func testEveryDestinationIsARegisteredRoute() {
        let known = Set(PassengerRoute.staticRoutes.map(\.path))
        let all: [PassengerDestination] = [.onboarding, .login, .profileSetup, .locationPermission, .liveMap]
        for destination in all {
            XCTAssertTrue(known.contains(destination.route.path), "\(destination) points at an unregistered path")
        }
    }

    /// The five pre-session screens the shell draws in place of the tab bar are exactly the four
    /// first-run destinations plus the splash. If those two sets ever disagreed, a passenger would
    /// either see a tab bar on the OTP screen or lose it on the map.
    func testTheFirstRunDestinationsAreThePreSessionRoutesLessTheMap() {
        let preSession = Set(PassengerRoute.staticRoutes.filter(\.isPreSession).map(\.path))
        let firstRun: Set<String> = [
            PassengerDestination.onboarding.route.path,
            PassengerDestination.login.route.path,
            PassengerDestination.profileSetup.route.path,
            PassengerDestination.locationPermission.route.path,
        ]
        XCTAssertEqual(preSession.subtracting([PassengerRoute.splash.path]), firstRun)
        XCTAssertFalse(preSession.contains(PassengerDestination.liveMap.route.path))
    }
}

/// SCR-PI-001 — the boot decision.
@MainActor
final class SplashModelTests: XCTestCase {

    private var sessions: FakePassengerSessions!
    private var profiles: FakePassengerProfileRepository!
    private var rides: FakeActiveRideLookup!
    private var preferences: FakeAppPreferences!

    override func setUp() {
        super.setUp()
        sessions = FakePassengerSessions()
        profiles = FakePassengerProfileRepository()
        rides = FakeActiveRideLookup()
        preferences = FakeAppPreferences()
    }

    private func model() -> SplashModel {
        SplashModel(sessions: sessions, profiles: profiles, rides: rides, preferences: preferences)
    }

    /// **The session is restored before anything is decided.** Without it a passenger who was signed
    /// in yesterday is signed out today, because the Keychain has not been read.
    func testItRestoresTheSessionFirst() async {
        let model = model()
        await model.decide()

        XCTAssertEqual(sessions.restoreCount, 1)
    }

    /// A first run asks nothing of the network at all: no profile read, no ride read. That is the
    /// whole reason the router takes `firstRunComplete` first.
    func testAFirstRunMakesNoNetworkCall() async {
        let model = model()
        await model.decide()

        XCTAssertEqual(model.route, .onboarding)
        XCTAssertEqual(profiles.meCount, 0)
        XCTAssertTrue(rides.lookups.isEmpty)
    }

    func testASignedOutPassengerWithALanguageGoesToLogin() async {
        preferences.language = Language.si
        let model = model()

        await model.decide()

        XCTAssertEqual(model.route, .login)
        XCTAssertEqual(profiles.meCount, 0, "there is nobody to have a profile")
    }

    /// **A failed profile read answers `true` here** — the passenger signed in before, so they have
    /// been through Profile Setup, and putting them back on an onboarding form because of a flat
    /// tunnel is the worse mistake. ``LoginModelTests`` asserts the opposite default on the other
    /// side.
    func testAFailedProfileReadDoesNotStrandAWorkingPassengerOnAForm() async {
        preferences.language = Language.si
        preferences.locationRationaleAcknowledged = true
        sessions.isSignedIn = true
        sessions.userId = Fixtures.passengerId
        profiles.profile = nil
        let model = model()

        await model.decide()

        XCTAssertEqual(model.route, .liveMap)
    }

    func testAProfileWithNoNameGoesToProfileSetup() async {
        preferences.language = Language.si
        sessions.isSignedIn = true
        sessions.userId = Fixtures.passengerId
        profiles.profile = Fixtures.profile(firstName: nil)
        let model = model()

        await model.decide()

        XCTAssertEqual(model.route, .profileSetup)
    }

    /// A name of spaces is not a name.
    func testABlankNameGoesToProfileSetup() async {
        preferences.language = Language.si
        sessions.isSignedIn = true
        sessions.userId = Fixtures.passengerId
        profiles.profile = Fixtures.profile(firstName: "   ")
        let model = model()

        await model.decide()

        XCTAssertEqual(model.route, .profileSetup)
    }

    /// US-1.14. A ride in flight beats the map — but only from the map: somebody who still owes a
    /// name is finishing that first, and the ride is not going anywhere.
    func testAnActiveRideIsResumedOnlyFromTheMap() async {
        preferences.language = Language.si
        preferences.locationRationaleAcknowledged = true
        sessions.isSignedIn = true
        sessions.userId = Fixtures.passengerId
        rides.rideId = Fixtures.rideId
        let model = model()

        await model.decide()

        XCTAssertEqual(model.route, .activeRide(rideId: Fixtures.rideId))
        XCTAssertEqual(rides.lookups, [Fixtures.passengerId])
    }

    func testTheRideIsNotLookedUpBeforeTheRationaleHasBeenShown() async {
        preferences.language = Language.si
        preferences.locationRationaleAcknowledged = false
        sessions.isSignedIn = true
        sessions.userId = Fixtures.passengerId
        rides.rideId = Fixtures.rideId
        let model = model()

        await model.decide()

        XCTAssertEqual(model.route, .locationPermission)
        XCTAssertTrue(rides.lookups.isEmpty)
    }

    /// **A failed ride read answers `nil`** — the opposite default to the profile read, and for the
    /// opposite reason: landing on the map is recovered the moment SCR-PI-010's socket connects,
    /// where a ride screen with no ride is not recovered at all.
    func testAFailedRideReadLandsOnTheMap() async {
        preferences.language = Language.si
        preferences.locationRationaleAcknowledged = true
        sessions.isSignedIn = true
        sessions.userId = Fixtures.passengerId
        rides.rideId = nil
        let model = model()

        await model.decide()

        XCTAssertEqual(model.route, .liveMap)
    }

    /// `.task` may run again after a scene change, and deciding twice would navigate twice.
    func testDecidingIsIdempotent() async {
        let model = model()
        await model.decide()
        await model.decide()

        XCTAssertEqual(sessions.restoreCount, 1)
    }
}

/// SCR-PI-002 — the carousel and the language picker.
@MainActor
final class OnboardingModelTests: XCTestCase {

    private var repository: FakeOnboardingRepository!

    override func setUp() {
        super.setUp()
        repository = FakeOnboardingRepository()
        PassengerLocale.apply(nil)
    }

    override func tearDown() {
        // `PassengerLocale` re-points a process-wide bundle; leaving it set would draw the next
        // test class's strings in whatever language this one chose.
        PassengerLocale.apply(nil)
        super.tearDown()
    }

    /// AL-26: Sinhala is the **default**, not a translation of English, and it is first.
    func testSinhalaIsPreSelectedAndFirst() {
        let model = OnboardingModel(onboarding: repository)

        XCTAssertEqual(model.state.language, Language.si)
        XCTAssertEqual(LanguageDisplay.choices, [Language.si, Language.ta, Language.en])
        XCTAssertEqual(LanguageDisplay.default, Language.si)
    }

    /// US-1.3's endonyms are **data, not copy** — the same string in all three locales, which is why
    /// they are not in `Localizable.strings`.
    func testTheRowsAreEndonymsWithAnEnglishGloss() {
        XCTAssertEqual(LanguageDisplay.endonym(Language.si), "සිංහල")
        XCTAssertEqual(LanguageDisplay.endonym(Language.ta), "தமிழ்")
        XCTAssertEqual(LanguageDisplay.endonym(Language.en), "English")
        XCTAssertEqual(LanguageDisplay.englishName(Language.si), "Sinhala")
        XCTAssertEqual(LanguageDisplay.englishName(Language.ta), "Tamil")
        XCTAssertNil(LanguageDisplay.englishName(Language.en), "the gloss would repeat the endonym")
    }

    /// **The choice is stored on the tap**, not on Get Started: the flow continues to Login whether
    /// or not the handset can reach the gateway.
    func testATapStoresTheChoiceImmediately() {
        let model = OnboardingModel(onboarding: repository)

        model.select(Language.ta)

        XCTAssertEqual(repository.chosen, [Language.ta])
        XCTAssertEqual(model.state.language, Language.ta)
    }

    /// **Get Started writes unconditionally**, including for a passenger who accepted the Sinhala
    /// default without touching a box — accepting it *is* answering the screen, and without this
    /// write the router sends them straight back here on the next cold start.
    func testGetStartedRecordsTheDefaultNobodyTouched() {
        let model = OnboardingModel(onboarding: repository)

        model.finish()

        XCTAssertEqual(repository.chosen, [Language.si])
        XCTAssertTrue(repository.stored != nil, "firstRunComplete is derived from a stored language")
    }

    /// BR-25.1: the carousel is presentation only, so the bundled three are on screen before
    /// content-svc has answered and stay there when it never does.
    func testTheCarouselFallsBackToTheBundledThree() async {
        repository.slidesResponse = []
        let model = OnboardingModel(onboarding: repository)

        await model.start()

        XCTAssertEqual(model.state.slides.count, FeatureSlides.count)
        XCTAssertEqual(model.state.slides.map(\.slot), [1, 2, 3])
    }

    /// The payload carries all three languages precisely because the picker is on this screen — so a
    /// language change re-resolves rather than re-fetching.
    func testALanguageChangeReResolvesTheServerSlidesWithoutRefetching() async {
        repository.slidesResponse = [
            Fixtures.slide(slot: 1, title: "Live", body: "Body"),
            Fixtures.slide(slot: 2, title: "Transit", body: "Body"),
        ]
        let model = OnboardingModel(onboarding: repository)
        await model.start()

        XCTAssertEqual(model.state.slides.first?.title, "Live SI", "Sinhala is the default")

        model.select(Language.en)

        XCTAssertEqual(model.state.slides.first?.title, "Live")
        XCTAssertEqual(model.state.slides.count, 2, "the server's count, not the fallback's")
    }

    /// content-svc serves a *reference*, never bytes, and this app ships no remote image loader — so
    /// the panel is captioned with the ref, which is more use to whoever wires the artwork than a
    /// blank box.
    func testAServerSlideIsCaptionedWithItsIllustrationReference() {
        let resolved = FeatureSlides.resolve(
            Fixtures.slide(slot: 2, title: "T", body: "B", ref: "onboarding_transit"),
            language: Language.en
        )

        XCTAssertEqual(resolved.caption, "onboarding_transit")
        XCTAssertEqual(resolved.symbolName, FeatureSlides.symbolName(forSlot: 2))
    }

    /// A fourth slide is a content change rather than an error: it draws with the first slot's
    /// symbol instead of an empty panel.
    func testASlotBeyondTheThreeStillGetsASymbol() {
        XCTAssertEqual(FeatureSlides.symbolName(forSlot: 9), FeatureSlides.symbolName(forSlot: 1))
    }
}
