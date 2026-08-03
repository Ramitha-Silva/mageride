package lk.mageride.driver.home

import kotlinx.coroutines.CancellationException
import lk.mageride.shared.data.api.dispatch.DispatchApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.api.subscription.SubscriptionApi
import lk.mageride.shared.data.api.wallet.WalletApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.DirectionalFilterCleared
import lk.mageride.shared.data.models.dispatch.DirectionalFilterCreated
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import lk.mageride.shared.data.models.dispatch.GoOnlineRequest
import lk.mageride.shared.data.models.dispatch.PresenceState
import lk.mageride.shared.data.models.dispatch.SetDirectionalFilterRequest
import lk.mageride.shared.data.models.query.EarningsPeriod
import lk.mageride.shared.data.models.query.EarningsSummary
import lk.mageride.shared.data.models.subscription.DailyFeeDayStatus
import lk.mageride.shared.data.models.subscription.TodaysDailyFee
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.domain.wallet.WalletAlert
import lk.mageride.shared.domain.wallet.WalletRules
import lk.mageride.shared.domain.wallet.WalletStanding

/**
 * Everything the SCR-DA-010 status header and sheet print, read in one pass.
 *
 * @property level The Driver Level badge (US-6A.14); `null` while reputation has not answered.
 * @property wallet The read-only balance in the app bar (US-9.7).
 * @property dailyFee Today's fee chip — PAID/UNPAID, the rate, and the first-trip-free flag
 *   (US-9.1, US-9.7).
 * @property earnings *"Today: 4 trips · Rs 3,180"*.
 * @property directional The Directional chip's state (DT-08).
 */
internal data class DriverStanding(
    val level: Int? = null,
    val wallet: Wallet? = null,
    val dailyFee: TodaysDailyFee? = null,
    val earnings: EarningsSummary? = null,
    val directional: DirectionalFilterState? = null,
) {

    /** US-9.9's `< Rs 200` nudge, evaluated by `:shared`'s rules rather than by a screen. */
    val walletAlert: WalletAlert
        get() = wallet?.let { WalletRules.alertFor(WalletStanding.of(it)) } ?: WalletAlert.None

    /**
     * Today's fee as money, for the chip and for the 2nd-trip warning.
     *
     * `TodaysDailyFee` carries no currency of its own — `subscription.yaml` declares the row as a
     * bare `dailyRateMinor`, and LKR is the only currency the platform transacts in
     * (`Money`'s own default, which is a `const` in `_shared.yaml`).
     */
    val dailyRate: Money?
        get() = dailyFee?.let { Money(amountMinor = it.dailyRateMinor) }

    /**
     * **US-9.1 — "wallet insufficient for 2nd trip".**
     *
     * The first trip of the day is free (D-08 waives it), so the gate bites on the *next* one: once
     * a trip has been taken and the fee is still unpaid, the wallet has to hold the day's rate or
     * `POST …/accept` answers `402 insufficient-wallet` fifteen seconds after an offer arrives.
     * Warning about it on the standby sheet is what stops that being the first the driver hears.
     */
    val cannotAffordNextTrip: Boolean
        get() {
            val fee = dailyFee ?: return false
            val rate = dailyRate ?: return false
            if (fee.status == DailyFeeDayStatus.PAID) return false
            if (fee.firstTripFree && fee.tripsToday == 0) return false
            return wallet?.let { !WalletStanding.of(it).canAfford(rate) } == true
        }
}

/**
 * dispatch-svc, wallet-svc, subscription-svc and query-svc as SCR-DA-010 and SCR-DA-013 use them.
 *
 * **The fence (R-01).** Everything here is standby, money and reputation — the ride aggregate is
 * ride-svc's and is reached through `OfferSession` and `ActiveRideRepository`, and a Mode A/B
 * tracking session is trip-state-svc's and is reached through [JourneyRepository]. Nothing in this
 * file may cross either boundary.
 *
 * **A dead read must not blank the dashboard.** The five standing reads are independent — a driver
 * whose reputation service is down still needs their wallet, their fee and their go-online toggle
 * — so each is attempted separately and a failure leaves its own field `null` rather than throwing
 * the screen away. Going online is the opposite: it is one call whose failure is the answer.
 */
internal class StandbyRepository(
    private val dispatch: DispatchApi,
    private val wallet: WalletApi,
    private val subscription: SubscriptionApi,
    private val query: QueryApi,
) {

    /** The whole status header and sheet, best-effort per field. */
    suspend fun standing(driverId: Ulid): DriverStanding = DriverStanding(
        level = read { dispatch.getDriverLevel(driverId).level },
        wallet = read { wallet.getWallet(driverId) },
        dailyFee = read { subscription.getTodaysDailyFee(driverId) },
        earnings = read { query.getDriverEarnings(driverId, EarningsPeriod.TODAY) },
        directional = read { dispatch.getDirectionalFilter() },
    )

    /**
     * `POST /v1/standby/online` (US-6A.1).
     *
     * `403 vehicle-not-approved` for a vehicle that has not cleared onboarding, and
     * `409 driver-already-live` when a session or ride is already running — both are the honest
     * answer to a toggle the driver just moved, so neither is swallowed.
     */
    suspend fun goOnline(vehicleId: Ulid, position: GeoPoint, driverHome: GeoPoint? = null): PresenceState =
        dispatch.goOnline(GoOnlineRequest(vehicleId = vehicleId, position = position, driverHome = driverHome)).state

    /** `POST /v1/standby/offline`. **Clears any active Directional filter** server-side (DT-04). */
    suspend fun goOffline(): PresenceState = dispatch.goOffline().state

    /** `GET /v1/standby/directional` — the card's whole state (DT-08). */
    suspend fun directional(): DirectionalFilterState = dispatch.getDirectionalFilter()

    /**
     * `POST /v1/standby/directional` (DT-01/DT-03).
     *
     * `403 not-online` off standby and `409 directional-limit-reached` once the day's uses are
     * gone; SCR-DA-013 disables **Set** on the second, which is why the count is read first.
     */
    suspend fun setDirectional(destination: GeoPoint, label: String?): DirectionalFilterCreated =
        dispatch.setDirectionalFilter(SetDirectionalFilterRequest(destination = destination, label = label))

    /**
     * `DELETE /v1/standby/directional` — **and the use is still spent** (US-6A.19).
     *
     * The response carries the same `usesRemaining` it had before, which is the anti-gaming rule
     * made visible: without it a driver could flick the filter on for the one offer they wanted and
     * off again, all day, on two uses.
     */
    suspend fun clearDirectional(): DirectionalFilterCleared = dispatch.clearDirectionalFilter()

    /** Attempts [block], answering `null` for anything short of cancellation. */
    @Suppress("TooGenericExceptionCaught") // One dead read must not take the other four with it.
    private suspend fun <T> read(block: suspend () -> T): T? = try {
        block()
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        null
    }
}
