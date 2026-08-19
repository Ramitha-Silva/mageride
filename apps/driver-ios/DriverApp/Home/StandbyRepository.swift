import Foundation
import MageRideShared

/// Everything the SCR-DI-010 status header and sheet print, read in one pass.
///
/// - Parameters:
///   - level: The Driver Level badge (US-6A.14); `nil` while reputation has not answered.
///   - wallet: The read-only balance in the navigation bar (US-9.7).
///   - dailyFee: Today's fee chip — PAID/UNPAID, the rate and the first-trip-free flag (US-9.1,
///     US-9.7).
///   - earnings: *"Today: 4 trips · Rs 3,180"*.
///   - directional: The Directional chip's state (DT-08).
struct DashboardStanding {

    var level: Int?
    var wallet: Wallet?
    var dailyFee: TodaysDailyFee?
    var earnings: EarningsSummary?
    var directional: DirectionalFilterState?

    /// US-9.9's `< Rs 200` nudge, evaluated by `:shared`'s rules rather than by a screen.
    ///
    /// Through ``walletAlertFor(wallet:)`` rather than `WalletRules.alertFor` directly: the threshold is
    /// a **defaulted** Kotlin parameter and defaults do not survive the export, so reaching the rule
    /// from Swift would mean spelling Rs 200 here — a second copy of the one number §9.4 makes
    /// admin-configurable.
    var walletAlert: WalletAlert? {
        wallet.map { IosWalletAlertKt.walletAlertFor(wallet: $0) }
    }

    /// US-9.9's low-balance threshold, when that is the alert in force.
    var lowBalanceThresholdMinor: Int64? {
        (walletAlert as? WalletAlertLowBalance)?.threshold.amountMinor
    }

    /// Today's fee as minor units, for the chip and for the 2nd-trip warning.
    ///
    /// `TodaysDailyFee` carries no currency of its own — `subscription.yaml` declares the row as a
    /// bare `dailyRateMinor`, and LKR is the only currency the platform transacts in.
    var dailyRateMinor: Int64? { dailyFee?.dailyRateMinor }

    /// **US-9.1 — "wallet insufficient for 2nd trip".**
    ///
    /// The first trip of the day is free (D-08 waives it), so the gate bites on the *next* one: once
    /// a trip has been taken and the fee is still unpaid, the wallet has to hold the day's rate or
    /// `POST …/accept` answers `402 insufficient-wallet` fifteen seconds after an offer arrives.
    /// Warning about it on the standby sheet is what stops that being the first the driver hears.
    var cannotAffordNextTrip: Bool {
        guard let fee = dailyFee, let wallet else { return false }
        if fee.status == DailyFeeDayStatus.paid { return false }
        if fee.firstTripFree && fee.tripsToday == 0 { return false }
        let rate = Money(amountMinor: fee.dailyRateMinor, currency: wallet.currency)
        return !WalletStanding.companion.of(wallet: wallet).canAfford(amount: rate)
    }

    /// Whether US-9.1's note belongs on SCR-DI-014 — the **second** trip, while the day is unpaid.
    var showsOfferFeeNote: Bool {
        guard let fee = dailyFee else { return false }
        return fee.tripsToday > 0 && fee.status == DailyFeeDayStatus.unpaid
    }
}

/// dispatch-svc, wallet-svc, subscription-svc and query-svc as SCR-DI-010 and SCR-DI-013 use them.
///
/// **The fence (R-01).** Everything here is standby, money and reputation — the ride aggregate is
/// ride-svc's and is reached through `OfferSession` and ``ActiveRideRepository``, and a Mode A/B
/// tracking session is trip-state-svc's and is reached through ``JourneyRepository``. Nothing behind
/// this protocol may cross either boundary.
protocol StandbyRepository: AnyObject {

    /// The whole status header and sheet, best-effort per field.
    func standing(driverId: String) async -> DashboardStanding

    /// `POST /v1/standby/online` (US-6A.1).
    func goOnline(vehicleId: String, position: GeoPoint) async throws -> PresenceState

    /// `POST /v1/standby/offline`. **Clears any active Directional filter** server-side (DT-04).
    func goOffline() async throws -> PresenceState

    /// `GET /v1/standby/directional` — the card's whole state (DT-08).
    func directional() async throws -> DirectionalFilterState

    /// `POST /v1/standby/directional` (DT-01/DT-03). Consumes one of the day's uses.
    func setDirectional(destination: GeoPoint, label: String?) async throws -> DirectionalFilterCreated

    /// `DELETE /v1/standby/directional` — **and the use is still spent** (US-6A.19).
    func clearDirectional() async throws -> DirectionalFilterCleared

    /// The driver's saved ★ Home / ★ Work shortcuts, for SCR-DI-013's destination field.
    func savedShortcuts() async -> [SavedAddress]

    /// query-svc's geocoder, biased toward where the driver is.
    func searchPlaces(query: String, near: Fix?) async throws -> [GeocodedPlace]
}

/// ``StandbyRepository`` over the four typed clients.
///
/// **A dead read must not blank the dashboard.** The five standing reads are independent — a driver
/// whose reputation service is down still needs their wallet, their fee and their go-online toggle —
/// so each is attempted separately and a failure leaves its own field `nil` rather than throwing the
/// screen away. Going online is the opposite: it is one call whose failure is the answer.
final class ApiStandbyRepository: StandbyRepository {

    private let dispatch: DispatchApi
    private let wallet: WalletApi
    private let subscription: SubscriptionApi
    private let query: QueryApi
    private let iam: IamApi

    init(dispatch: DispatchApi, wallet: WalletApi, subscription: SubscriptionApi, query: QueryApi, iam: IamApi) {
        self.dispatch = dispatch
        self.wallet = wallet
        self.subscription = subscription
        self.query = query
        self.iam = iam
    }

    func standing(driverId: String) async -> DashboardStanding {
        DashboardStanding(
            level: await read { Int(try await self.dispatch.getDriverLevel(driverId: driverId).level) },
            wallet: await read { try await self.wallet.getWallet(userId: driverId) },
            dailyFee: await read { try await self.subscription.getTodaysDailyFee(driverId: driverId) },
            earnings: await read {
                try await self.query.getDriverEarnings(driverId: driverId, period: EarningsPeriod.today)
            },
            directional: await read { try await self.dispatch.getDirectionalFilter() }
        )
    }

    /// `403 vehicle-not-approved` for a vehicle that has not cleared onboarding, and
    /// `409 driver-already-live` when a session or ride is already running — both are the honest
    /// answer to a toggle the driver just moved, so neither is swallowed.
    ///
    /// `driverHome` is deliberately not sent. It is DT-06's *"heading home"* hint and the only saved
    /// address that could supply it is the driver's own ★ Home, which SCR-DI-013 already sets as a
    /// **destination** when they want that behaviour; sending it on every go-online would apply a
    /// directional bias the driver never asked for.
    func goOnline(vehicleId: String, position: GeoPoint) async throws -> PresenceState {
        try await dispatch.goOnline(
            request: GoOnlineRequest(vehicleId: vehicleId, position: position, driverHome: nil),
            idempotencyKey: nil
        ).state
    }

    func goOffline() async throws -> PresenceState {
        try await dispatch.goOffline(idempotencyKey: nil).state
    }

    func directional() async throws -> DirectionalFilterState {
        try await dispatch.getDirectionalFilter()
    }

    /// `403 not-online` off standby and `409 directional-limit-reached` once the day's uses are gone;
    /// SCR-DI-013 disables **Set** on the second, which is why the count is read first.
    func setDirectional(destination: GeoPoint, label: String?) async throws -> DirectionalFilterCreated {
        try await dispatch.setDirectionalFilter(
            request: SetDirectionalFilterRequest(destination: destination, label: label),
            idempotencyKey: nil
        )
    }

    /// The response carries the same `usesRemaining` it had before, which is the anti-gaming rule
    /// made visible: without it a driver could flick the filter on for the one offer they wanted and
    /// off again, all day, on two uses.
    func clearDirectional() async throws -> DirectionalFilterCleared {
        try await dispatch.clearDirectionalFilter()
    }

    /// ★ Home and ★ Work. Best-effort — the destination field still works without them.
    func savedShortcuts() async -> [SavedAddress] {
        let addresses = await read { try await self.iam.listSavedAddresses().items } ?? []
        return addresses.filter { $0.isHome?.boolValue == true || $0.isWork?.boolValue == true }
    }

    func searchPlaces(query text: String, near: Fix?) async throws -> [GeocodedPlace] {
        try await query.searchPlaces(
            query: text,
            lat: near.map { KotlinDouble(value: $0.lat) },
            lng: near.map { KotlinDouble(value: $0.lng) },
            limit: KotlinInt(value: Int32(Self.searchLimit))
        ).places
    }

    /// Attempts [block], answering `nil` for anything that throws.
    ///
    /// One dead read must not take the other four with it. Nothing is logged or surfaced: a level
    /// badge that is absent for a minute is a badge that is absent, and a dashboard that refused to
    /// draw because reputation was down would be a driver who cannot go to work.
    private func read<T>(_ block: () async throws -> T) async -> T? {
        try? await block()
    }

    /// Six hits — as many as fit above the keyboard on the smallest supported handset.
    private static let searchLimit = 6
}
