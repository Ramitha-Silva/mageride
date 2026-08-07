import MageRideShared
import SwiftUI

/// **Home** — SCR-DI-010 when the live vehicle is Mode C, SCR-DI-011 when it is Mode A or Mode B.
///
/// One destination, because D2' makes them one: SCR-DI-012 was merged into SCR-DI-010 and SCR-DI-011
/// *"IS the driver's home dashboard"* for a bus or a private-transport vehicle. What differs between
/// them is the sheet and the header; the map, the banners, the tab bar and the offer takeover are
/// shared, and a chooser in front of two destinations would have to keep all four in step.
///
/// **SCR-DI-014 arrives here as a takeover, not as a route** — a `fullScreenCover` with the
/// interactive dismiss disabled. That is what ``PushRouter`` means by routing a `ride_offer` push to
/// Home: the offer is the dashboard's, and fifteen seconds is not long enough to navigate anywhere.
///
/// **AL-31: no hamburger.** There is no leading navigation-bar item on this screen and there is no
/// drawer behind one; navigation is the tab bar's **Menu** tab, which ``DriverShell`` draws.
///
/// - Parameters:
///   - onOpenRide: SCR-DI-015. Reached three ways — winning an offer, and a cold start or a refresh
///     that found a ride already in hand.
///   - onOpenLevel: SCR-DI-019, from the `L3` badge (C090). D2' §SCR-DI-019's traceability row is
///     *"Driver Level badge | SCR-DI-019 + SCR-DI-010 badge"* — the badge is the level screen's entry
///     point, and it is the only one the wireframes draw.
///   - onOpenEarnings: SCR-DI-020, from the *"Today: 4 trips · Rs 3,180"* line on the standby sheet.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct HomeScreen: View {

    @StateObject private var model: HomeModel
    @StateObject private var offers: OfferModel

    private let onOpenDirectional: () -> Void
    private let onOpenVehicles: () -> Void
    private let onOpenRide: (String) -> Void
    private let onOpenLevel: () -> Void
    private let onOpenEarnings: () -> Void

    init(
        home: @autoclosure @escaping () -> HomeModel,
        offers: @autoclosure @escaping () -> OfferModel,
        onOpenDirectional: @escaping () -> Void,
        onOpenVehicles: @escaping () -> Void,
        onOpenRide: @escaping (String) -> Void,
        onOpenLevel: @escaping () -> Void,
        onOpenEarnings: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: home())
        _offers = StateObject(wrappedValue: offers())
        self.onOpenDirectional = onOpenDirectional
        self.onOpenVehicles = onOpenVehicles
        self.onOpenRide = onOpenRide
        self.onOpenLevel = onOpenLevel
        self.onOpenEarnings = onOpenEarnings
    }

    var body: some View {
        VStack(spacing: 0) {
            banners

            ZStack {
                DriverHomeMap(position: model.state.position, token: model.state.liveToken)

                // D2' §SCR-DI-010: "Offline → grey overlay + 'Go online to receive rides'". Only on
                // the Mode C standby map — a Mode A/B dashboard has no standby to be off.
                if !model.state.isOnline && !model.state.isScheduledMode && !model.state.isLoading {
                    offlineScrim
                }
            }

            if model.state.isScheduledMode {
                JourneySheet(
                    state: model.state,
                    onStart: { Task { await model.startJourney() } },
                    onEndOrRestart: { Task { await model.endOrRestartJourney() } },
                    onChooseRoute: { routeId in Task { await model.chooseRoute(routeId) } },
                    onAutoEndChanged: model.setAutoEndAtDestination
                )
            } else {
                StandbySheet(
                    state: model.state,
                    onToggleOnline: { desired in Task { await model.toggleOnline(desired) } },
                    onOpenDirectional: onOpenDirectional,
                    onOpenVehicles: onOpenVehicles,
                    onOpenEarnings: onOpenEarnings
                )
            }
        }
        .background(MageRideColor.background)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { statusBar }
        .task {
            model.start()
            offers.start()
            await model.refresh()
        }
        .onDisappear {
            model.stop()
            offers.stop()
        }
        // A ride already in hand outranks everything on this screen — SCR-DI-001's router resumes it,
        // and a driver who closed the app mid-trip must not land on a standby map that says they idle.
        .onChange(of: model.state.activeRideId) { rideId in
            guard let rideId else { return }
            model.consumeActiveRide()
            onOpenRide(rideId)
        }
        // Presented from the offer slot rather than from a `@State` flag, and the setter is empty on
        // purpose: **the driver does not dismiss SCR-DI-014**, the two buttons and the clock do. The
        // binding is re-read on every change of `offers.state`, so the cover comes down when the slot
        // does — see `OfferTakeover`'s own `.interactiveDismissDisabled()`.
        .fullScreenCover(isPresented: Binding(get: { isOfferShowing }, set: { _ in })) {
            OfferTakeover(
                state: offers.state,
                showsFeeNote: model.state.standing.showsOfferFeeNote,
                feeAmount: model.state.standing.dailyRateMinor.map { MoneyFormat.rupees($0) } ?? MoneyFormat.empty,
                isDirectional: model.state.standing.directional?.active == true,
                onAccept: { Task { await offers.accept() } },
                onReject: { Task { await offers.reject() } },
                onFinished: { rideId in
                    offers.consumeOutcome()
                    Task { await model.refresh() }
                    if let rideId { onOpenRide(rideId) }
                }
            )
        }
    }

    /// Whether SCR-DI-014 is up — a live offer, or an ending that still has to be explained.
    private var isOfferShowing: Bool {
        offers.state.isLive || OfferTakeover.outcomeMessageKey(offers.state.outcome) != nil
    }

    /// The status header — the Driver Level badge and the read-only wallet balance (US-9.7, US-6A.14).
    ///
    /// The wireframe also prints `★4.8`. **There is no app-facing read for a driver's own rating**:
    /// `dispatch.yaml` answers a level and a points total, `RideDriver.rating` is the *passenger's*
    /// view of a driver on a ride, and the reputation contract is portal-only (C012). Rendering the
    /// points behind a star would be a different number wearing the star's meaning, so the star is
    /// absent until a read exists. The same gap the C070 handoff records.
    @ToolbarContentBuilder
    private var statusBar: some ToolbarContent {
        ToolbarItem(placement: .navigationBarLeading) {
            if let level = model.state.standing.level {
                Button(action: onOpenLevel) {
                    SolidBadge(label: DashboardLabels.level(level), accent: MageRideColor.primary)
                }
                .buttonStyle(.plain)
            }
        }
        ToolbarItem(placement: .navigationBarTrailing) {
            Text(walletText)
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)
        }
    }

    /// Mode A pays no fee at all, so the balance is not what that driver's header is about.
    private var walletText: String {
        if model.state.vehicles.live?.mode == ServiceMode.a { return "journey_no_fee".localised }
        guard let wallet = model.state.standing.wallet else { return "" }
        return MoneyFormat.rupees(wallet.availableMinor)
    }

    /// The banner stack: the daily-fee chip (SCR-DI-010), the ignition notice (SCR-DI-011, AL-32), the
    /// low-balance nudge (US-9.9) and the 2nd-trip fee warning (US-9.1).
    ///
    /// The order is the wireframe's — money first, because a driver who cannot accept the next ride
    /// needs to know before they read anything else.
    @ViewBuilder
    private var banners: some View {
        VStack(spacing: MageRideSpacing.xxs) {
            if !model.state.isScheduledMode, let fee = model.state.standing.dailyFee {
                DashboardBanner(
                    text: HomeScreen.dailyFeeText(fee),
                    accent: fee.status == DailyFeeDayStatus.paid ? MageRideColor.success : MageRideColor.warning,
                    symbolName: "banknote"
                )
            }

            // AL-32 — the tracker opened this session on ignition, and the dashboard says so rather
            // than pretending the driver did it. End Journey stays live regardless.
            if model.state.isScheduledMode, model.state.journey.startedByDevice, model.state.journey.isRunning {
                DashboardBanner(
                    text: "journey_ignition_banner".localised,
                    accent: MageRideColor.secondary,
                    symbolName: "fuelpump.fill"
                )
            }

            if model.state.journey.isRestartable {
                DashboardBanner(text: "journey_auto_ended".localised, accent: MageRideColor.warning)
            }

            if let threshold = model.state.standing.lowBalanceThresholdMinor {
                DashboardBanner(
                    text: "home_low_balance".localisedFormat(MoneyFormat.rupees(threshold)),
                    accent: MageRideColor.warning
                )
            }

            if model.state.standing.cannotAffordNextTrip {
                DashboardBanner(
                    text: "home_second_trip_fee".localisedFormat(
                        model.state.standing.dailyRateMinor.map { MoneyFormat.rupees($0) } ?? MoneyFormat.empty
                    ),
                    accent: MageRideColor.error
                )
            }

            if let errorKey = model.state.errorKey {
                DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                    .onTapGesture(perform: model.dismissError)
            }
        }
    }

    /// The grey overlay an offline standby map wears (D2' §SCR-DI-010).
    private var offlineScrim: some View {
        Color.black.opacity(HomeScreen.scrimOpacity)
            .overlay {
                Text(key: "home_offline_hint")
                    .mageFont(.subtitle)
                    .foregroundStyle(MageRideColor.onStatus)
                    .multilineTextAlignment(.center)
                    .padding(MageRideSpacing.lg)
            }
            .allowsHitTesting(false)
    }

    private static func dailyFeeText(_ fee: TodaysDailyFee) -> String {
        let amount = MoneyFormat.rupees(fee.dailyRateMinor)
        if fee.status == DailyFeeDayStatus.paid { return "home_daily_fee_paid".localisedFormat(amount) }
        if fee.firstTripFree { return "home_daily_fee_first_free".localisedFormat(amount) }
        return "home_daily_fee_due".localisedFormat(amount)
    }

    /// D2' §0.2's scrim opacity for a disabled surface behind a message.
    ///
    /// `Color.black` rather than a role: a scrim is a platform primitive and darkening `onSurface`
    /// would lighten it in dark mode, which is the opposite of what a scrim is for. The shadow token
    /// in ``MageRideElevation`` is built on the same primitive for the same reason.
    private static let scrimOpacity: Double = 0.45
}

/// The codes on this screen that are **data, not copy**.
///
/// `L3` is a Driver Level identifier, not a sentence: three identical values in the three
/// `Localizable.strings` files is exactly what `LocalizationTests` (correctly) fails on. Same rule
/// `Rs`, `+94` and the language endonyms follow (C086).
enum DashboardLabels {

    /// The Driver Level badge — `L1`…`L3` (US-6A.14).
    static func level(_ level: Int) -> String { "L\(level)" }
}
