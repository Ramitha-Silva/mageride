import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-021's rules** — the balance nobody computes, the two *"Top Up Required"* lines that are
/// different questions, and the threshold that lives on the handset.
@MainActor
final class WalletModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var wallet: FakeWalletRepository!
    private var preferences: FakeWalletPreferences!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        wallet = FakeWalletRepository()
        preferences = FakeWalletPreferences()
    }

    private func makeModel() -> WalletModel {
        WalletModel(identity: identity, wallet: wallet, preferences: preferences)
    }

    // MARK: - The balance is read, never computed

    func testTheHeadlineIsTheBalanceAndTheDecisionsUseTheSpendableFigure() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(balanceMinor: 30_000, availableMinor: 10_000, outstandingDebtMinor: 20_000)
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.balanceMinor, 30_000, "US-9.7 calls the headline the balance")
        XCTAssertEqual(model.state.availableMinor, 10_000, "every decision asks the D-05 net figure")
        XCTAssertEqual(model.state.outstandingDebtMinor, 20_000)
    }

    /// A driver with no accrued debt should not be reading two numbers for one wallet.
    func testTheDebtLineIsAbsentWhenThereIsNoDebt() async {
        wallet.standing = WalletFeeStanding(wallet: driverWallet(balanceMinor: 124_000, outstandingDebtMinor: 0))
        let model = makeModel()

        await model.refresh()

        XCTAssertNil(model.state.outstandingDebtMinor)
    }

    /// A dead fee read must not blank the balance — each of the three reads is best-effort.
    func testADeadFeeReadLeavesTheBalanceStanding() async {
        wallet.standing = WalletFeeStanding(wallet: driverWallet(balanceMinor: 124_000))
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.balanceMinor, 124_000)
        XCTAssertNil(model.state.standing.dailyFee, "the fee card says so rather than the screen failing")
        XCTAssertNil(model.state.errorKey)
        XCTAssertFalse(model.state.isLoading)
    }

    // MARK: - The rate the fee card prints

    /// The fee row is a snapshot of the day it was written and the schedule is what will be charged
    /// next; when they disagree the newer figure is the honest one.
    func testTheScheduleWinsOverTheFeeRowsOwnRate() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(),
            dailyFee: todaysDailyFee(dailyRateMinor: 10_000),
            schedule: feeSchedule(threeWheelerMinor: 15_000)
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.standing.dailyRateMinor, 15_000)
    }

    func testTheFeeRowsRateIsUsedWhenTheScheduleHasNoTierForTheVehicle() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(),
            dailyFee: todaysDailyFee(vehicleType: VehicleType.truck, dailyRateMinor: 40_000),
            schedule: feeSchedule()
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.standing.dailyRateMinor, 40_000, "§20 seeds no plan for a truck")
    }

    /// *"PAID ✓ (1st trip free)"* on a day the free trip has been spent would describe two days at once.
    func testTheFirstTripFreeQualifierIsGoneOnceTheFeeIsPaid() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(),
            dailyFee: todaysDailyFee(status: DailyFeeDayStatus.paid, tripsToday: 3, firstTripFree: true)
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertTrue(model.state.standing.isFeePaid)
        XCTAssertFalse(model.state.standing.isFirstTripStillFree)
    }

    // MARK: - The three banners, ranked

    func testOverdrawnIsD5sTopUpRequiredAndWinsOverEverything() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(balanceMinor: -5_000, availableMinor: -5_000),
            dailyFee: todaysDailyFee(),
            schedule: feeSchedule()
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.overdrawnByMinor, 5_000)
        XCTAssertFalse(model.state.isLowBalance, "overdrawn is a stronger state, not a louder one")
    }

    /// **D2' and D5' draw the same words at two different lines, and both are right.** §9.4's banner is
    /// a negative balance; §SCR-DI-021's is below one day's fee, which is US-9.1's real consequence.
    func testBelowOneDaysFeeIsItsOwnStateAndIsNotTheOverdrawnOne() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(balanceMinor: 5_000, availableMinor: 5_000),
            dailyFee: todaysDailyFee(dailyRateMinor: 10_000),
            schedule: feeSchedule(threeWheelerMinor: 10_000)
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertNil(model.state.overdrawnByMinor, "Rs 50 is not a negative balance")
        XCTAssertTrue(model.state.isBelowDayFee)
    }

    /// Trips 2..N are free after the deduction (US-9.4), so there is nothing left today to be short of.
    func testBelowTheDaysFeeIsFalseOnceTheFeeIsPaid() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(balanceMinor: 100, availableMinor: 100),
            dailyFee: todaysDailyFee(status: DailyFeeDayStatus.paid),
            schedule: feeSchedule()
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertFalse(model.state.isBelowDayFee)
    }

    func testTheLowBalanceNudgeIsTheSoftestOfTheThree() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(balanceMinor: 15_000, availableMinor: 15_000),
            dailyFee: todaysDailyFee(dailyRateMinor: 10_000),
            schedule: feeSchedule(threeWheelerMinor: 10_000)
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertNil(model.state.overdrawnByMinor)
        XCTAssertFalse(model.state.isBelowDayFee, "Rs 150 covers a Rs 100 fee")
        XCTAssertTrue(model.state.isLowBalance, "and is still under the Rs 200 default")
    }

    // MARK: - The driver's own threshold

    func testTheDefaultIsD5sRs200UntilTheDriverMovesIt() async {
        let model = makeModel()

        XCTAssertEqual(
            model.state.thresholdMinor,
            WalletRules.shared.DEFAULT_LOW_BALANCE_THRESHOLD.amountMinor
        )
        XCTAssertNil(preferences.lowBalanceThresholdMinor, "never chosen stays distinguishable from chose 200")
    }

    func testMovingTheThresholdMovesTheNudgeAndPersists() async {
        wallet.standing = WalletFeeStanding(
            wallet: driverWallet(balanceMinor: 40_000, availableMinor: 40_000),
            dailyFee: todaysDailyFee(dailyRateMinor: 10_000),
            schedule: feeSchedule(threeWheelerMinor: 10_000)
        )
        let model = makeModel()
        await model.refresh()
        XCTAssertFalse(model.state.isLowBalance, "Rs 400 is above the Rs 200 default")

        model.setThreshold(minor: 60_000)

        XCTAssertEqual(preferences.lowBalanceThresholdMinor, 60_000)
        XCTAssertTrue(model.state.isLowBalance, "a Rs 300/day van driver warned at Rs 600")
    }

    func testResettingPutsTheThresholdBackAndForgetsTheChoice() {
        let model = makeModel()
        model.setThreshold(minor: 60_000)

        model.clearThreshold()

        XCTAssertNil(preferences.lowBalanceThresholdMinor)
        XCTAssertEqual(
            model.state.thresholdMinor,
            WalletRules.shared.DEFAULT_LOW_BALANCE_THRESHOLD.amountMinor
        )
    }

    /// `UserDefaults.integer(forKey:)` answers `0` for an absent key, and zero is a threshold a driver
    /// could plausibly choose — which is why the store reads an object and the Android twin needs a
    /// `-1` sentinel.
    func testAThresholdOfZeroIsAChoiceAndNotAnAbsence() {
        let store = UserDefaults(suiteName: #function)!
        store.removePersistentDomain(forName: #function)
        let stored = UserDefaultsWalletPreferences(store: store)

        XCTAssertNil(stored.lowBalanceThresholdMinor)
        stored.lowBalanceThresholdMinor = 0
        XCTAssertEqual(stored.lowBalanceThresholdMinor, 0)
        XCTAssertEqual(stored.lowBalanceThreshold.amountMinor, 0)

        stored.lowBalanceThresholdMinor = nil
        XCTAssertNil(stored.lowBalanceThresholdMinor)
    }

    // MARK: -

    func testNothingIsReadWithoutASession() async {
        identity.driverId = nil
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(wallet.standingReads, 0)
    }
}
