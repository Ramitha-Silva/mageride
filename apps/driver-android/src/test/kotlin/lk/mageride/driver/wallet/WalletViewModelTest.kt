package lk.mageride.driver.wallet

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.subscription.DailyFeeRate
import lk.mageride.shared.data.models.subscription.DailyFeeRateList
import lk.mageride.shared.domain.wallet.WalletAlert
import lk.mageride.shared.domain.wallet.WalletRules
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-021 — the balance, the vehicle's own rate, and the three states below it.
 *
 * The interesting part of this screen is that D2' and D5' draw *"Top Up Required"* at two different
 * lines and both are right; see [WalletState.belowDayFee].
 */
class WalletViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val preferences = FakeWalletPreferences()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_card_prints_the_rate_for_the_drivers_own_vehicle() = runBlocking {
        backend.returns("getWallet", wallet(balanceMinor = 124_000L))
        backend.returns("getTodaysDailyFee", todaysFee(paid = true, tripsToday = 3))
        backend.returns("listDailyFeeRates", rates())

        val state = viewModel().state.await { !it.loading }

        assertEquals(Money.ofMinor(124_000L), state.balance)
        assertEquals(VehicleType.THREE_WHEELER, state.standing.dailyFee?.vehicleType)
        // D5' §2.1's three-wheeler tier, read from `GET /v1/fees/rates` rather than baked.
        assertEquals(Money.ofMinor(10_000L), state.standing.dailyRate)
        assertTrue(state.standing.feePaid)
    }

    @Test
    fun the_first_trip_free_qualifier_is_dropped_once_the_fee_has_been_taken() = runBlocking {
        // "PAID ✓ (1st trip free)" describes two different days at once: the waiver is spent by the
        // charge, so the qualifier belongs only to a day the fee is still unpaid (US-9.1, US-9.4).
        backend.returns("getWallet", wallet(balanceMinor = 124_000L))
        backend.returns("getTodaysDailyFee", todaysFee(paid = true, tripsToday = 2))
        backend.returns("listDailyFeeRates", rates())

        assertFalse(viewModel().state.await { !it.loading }.standing.firstTripStillFree)
    }

    @Test
    fun a_wallet_below_the_drivers_own_line_is_low_but_not_overdrawn() = runBlocking {
        preferences.lowBalanceThresholdMinor = 60_000L // Rs 600, not D5' §9.4's Rs 200.
        backend.returns("getWallet", wallet(balanceMinor = 45_000L))
        backend.returns("getTodaysDailyFee", todaysFee(paid = true))
        backend.returns("listDailyFeeRates", rates())

        val state = viewModel().state.await { !it.loading }

        assertEquals(Money.ofMinor(60_000L), state.threshold)
        val alert = assertIs<WalletAlert.LowBalance>(state.alert)
        assertEquals(Money.ofMinor(15_000L), alert.shortfall)
        assertFalse(state.belowDayFee, "today's fee is already paid — there is nothing left to be short of")
    }

    @Test
    fun a_negative_wallet_is_d5s_top_up_required() = runBlocking {
        // D5' §9.4's second clause. A balance goes below zero on a reversal or a cancellation
        // penalty, which is why the state exists at all.
        backend.returns("getWallet", wallet(balanceMinor = -3_500L))
        backend.returns("getTodaysDailyFee", todaysFee(paid = true))
        backend.returns("listDailyFeeRates", rates())

        val alert = assertIs<WalletAlert.TopUpRequired>(viewModel().state.await { !it.loading }.alert)
        assertEquals(Money.ofMinor(3_500L), alert.owed)
    }

    @Test
    fun a_wallet_short_of_one_days_fee_is_d2s_top_up_required() = runBlocking {
        // D2' §SCR-DA-021's own clause, and US-9.1's real consequence: the next request is refused
        // with `402 insufficient-wallet` fifteen seconds after an offer arrives.
        backend.returns("getWallet", wallet(balanceMinor = 4_000L))
        backend.returns("getTodaysDailyFee", todaysFee(paid = false, tripsToday = 1))
        backend.returns("listDailyFeeRates", rates())

        val state = viewModel().state.await { !it.loading }

        assertIs<WalletAlert.LowBalance>(state.alert, "and it is not yet overdrawn, which is a harder state")
        assertTrue(state.belowDayFee, "Rs 40 against a Rs 100 rate")
    }

    @Test
    fun the_spendable_balance_is_net_of_debt_and_the_displayed_one_is_not() = runBlocking {
        // D-05's accrued penalty. US-9.7 calls the headline figure the balance; every *decision*
        // in this cluster is checked against what is left after the debt.
        backend.returns("getWallet", wallet(balanceMinor = 30_000L, debtMinor = 20_000L))
        backend.returns("getTodaysDailyFee", todaysFee(paid = false, tripsToday = 1))
        backend.returns("listDailyFeeRates", rates())

        val state = viewModel().state.await { !it.loading }

        assertEquals(Money.ofMinor(30_000L), state.balance)
        assertEquals(Money.ofMinor(10_000L), state.available)
        assertFalse(state.belowDayFee, "Rs 100 spendable exactly covers the Rs 100 rate")
    }

    @Test
    fun a_dead_fee_read_still_leaves_the_balance_readable() = runBlocking {
        // Three independent reads across two services. A driver whose fee read failed still needs
        // to see their money — the rule `StandbyRepository` already follows for the dashboard.
        backend.returns("getWallet", wallet(balanceMinor = 124_000L))
        backend.fails("getTodaysDailyFee", HttpStatusCode.InternalServerError, "internal-error")
        backend.fails("listDailyFeeRates", HttpStatusCode.InternalServerError, "internal-error")

        val state = viewModel().state.await { !it.loading }

        assertEquals(Money.ofMinor(124_000L), state.balance)
        assertNull(state.standing.dailyFee)
        assertNull(state.standing.dailyRate)
        assertNull(state.error, "a best-effort read that failed is a blank field, not a banner")
    }

    @Test
    fun the_threshold_defaults_to_d5s_two_hundred_and_can_be_put_back() = runBlocking {
        backend.returns("getWallet", wallet(balanceMinor = 124_000L))
        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(WalletRules.DEFAULT_LOW_BALANCE_THRESHOLD, model.state.value.threshold)

        model.setThreshold(60_000L)
        assertEquals(Money.ofMinor(60_000L), model.state.value.threshold)
        assertEquals(60_000L, preferences.lowBalanceThresholdMinor, "it survives the screen")

        model.clearThreshold()
        assertEquals(WalletRules.DEFAULT_LOW_BALANCE_THRESHOLD, model.state.value.threshold)
        // Null rather than 20,000: "never chose one" has to stay distinguishable from "chose Rs 200"
        // for the day the platform gains a per-driver setting to migrate.
        assertNull(preferences.lowBalanceThresholdMinor)
    }

    /** D5' §2.1's seven tiers, as `GET /v1/fees/rates` sends them. */
    private fun rates() = DailyFeeRateList(
        listOf(
            DailyFeeRate(VehicleType.MOTORBIKE, dailyFeeMinor = 5_000, mode = ServiceMode.C),
            DailyFeeRate(VehicleType.THREE_WHEELER, dailyFeeMinor = 10_000, mode = ServiceMode.C),
            DailyFeeRate(VehicleType.VAN, dailyFeeMinor = 30_000, mode = ServiceMode.C),
        ),
    )

    private suspend fun viewModel(): WalletViewModel {
        val api = backend.mageRideApi()
        return main.own(
            WalletViewModel(
                identity = identity(backend, signedInSessions(backend)),
                wallet = WalletRepository(wallet = api.wallet, subscription = api.subscription),
                preferences = preferences,
            ),
        )
    }
}
