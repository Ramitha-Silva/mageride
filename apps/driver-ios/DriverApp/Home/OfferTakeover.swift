import AudioToolbox
import MageRideShared
import SwiftUI
import UIKit

/// **SCR-DI-014 · the incoming dispatch takeover** (US-6A.2/6A.3, R-02, E-01).
///
/// A `fullScreenCover` sized to the whole window rather than a route: the offer belongs to the
/// dashboard, fifteen seconds is not long enough to navigate, and a swipe must not dismiss it — a
/// driver who swipes out of an offer they meant to accept has lost it. `.interactiveDismissDisabled()`
/// is what makes that true on this platform, where a sheet is draggable by default and a `Dialog` (the
/// Android twin's shape) does not exist. The two ways out are the two buttons and the clock.
///
/// - Parameters:
///   - showsFeeNote: US-9.1's *"2nd trip today — the daily fee deducts on accept"*. Passed in from the
///     dashboard's own `GET /v1/fees/{driverId}/today` rather than read again here: it is the same
///     fact, and a second read inside the fifteen seconds would compete with the enrichment one.
///   - isDirectional: DT-08's badge. The `offer.created` envelope does not carry a `directionalMatched`
///     flag, so it is derived from the driver's **own** filter being active — which is sound, because
///     dispatch only offers direction-matching rides while one is (DT-01).
///   - onFinished: Called once with the won ride's id, or `nil` for every other ending.
struct OfferTakeover: View {

    let state: OfferUiState
    let showsFeeNote: Bool
    let feeAmount: String
    let isDirectional: Bool
    let onAccept: () -> Void
    let onReject: () -> Void
    let onFinished: (String?) -> Void

    var body: some View {
        ZStack {
            MageRideOfferColor.background.ignoresSafeArea()

            if let messageKey = OfferTakeover.outcomeMessageKey(state.outcome) {
                outcomeCard(messageKey)
            } else {
                content
            }
        }
        .interactiveDismissDisabled()
        // Keyed on the offer id so a redraw does not buzz again, and so a **second** offer does.
        .onChange(of: state.offer?.offerId) { offerId in
            if offerId != nil { OfferAlert.announce() }
        }
        .onAppear { if state.offer != nil { OfferAlert.announce() } }
        // Won navigates immediately; an expiry or a decline auto-dismisses, which is D2's own
        // "expired → auto-dismiss". Only the endings that need explaining stay on screen.
        .onChange(of: OfferTakeover.outcomeId(state.outcome)) { _ in
            guard let outcome = state.outcome else { return }
            switch outcome {
            case let won as OfferOutcomeWon: onFinished(won.ride.rideId)
            case is OfferOutcomeExpired, is OfferOutcomeDeclined: onFinished(nil)
            default: break
            }
        }
    }

    /// The wireframe's dark takeover: ring, badges, fare, the two places, the fee note, two buttons.
    private var content: some View {
        VStack(spacing: MageRideSpacing.sm) {
            CountdownRing(
                progress: state.progress,
                seconds: state.secondsLeft,
                isUrgent: state.isUrgent,
                labelColour: MageRideOfferColor.onOffer
            )
            .padding(.top, MageRideSpacing.xs)

            badges

            Text(MoneyFormat.rupees(state.fareMinor))
                .mageFont(.display)
                .foregroundStyle(MageRideOfferColor.onOffer)

            Text(key: PackageLabels.payment(state.detail?.paymentMethod))
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideOfferColor.muted)

            VStack(spacing: MageRideSpacing.xs) {
                placeRow(labelKey: "offer_pickup", symbolName: "circle.fill", value: state.detail?.pickup.address)
                placeRow(labelKey: "offer_drop", symbolName: "diamond.fill", value: state.detail?.dropoff.address)
            }
            .padding(MageRideSpacing.sm)
            .frame(maxWidth: .infinity)
            .background(
                MageRideOfferColor.surface,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )

            if showsFeeNote {
                Text("offer_fee_note".localisedFormat(feeAmount))
                    .mageFont(.label)
                    .foregroundStyle(MageRideOfferColor.accent)
                    .multilineTextAlignment(.center)
            }

            Spacer(minLength: MageRideSpacing.md)

            HStack(spacing: MageRideSpacing.xs) {
                Button(action: onReject) {
                    Text(key: "offer_reject")
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideOfferColor.onOffer)
                        .frame(maxWidth: .infinity, minHeight: MageRideControl.ctaHeight)
                        .overlay {
                            RoundedRectangle(cornerRadius: MageRideControl.ctaRadius, style: .continuous)
                                .strokeBorder(MageRideOfferColor.outline, lineWidth: 1)
                        }
                }
                .buttonStyle(.plain)
                .disabled(state.isDeciding)

                Button(action: onAccept) {
                    Text(key: "offer_accept")
                }
                .buttonStyle(.mageCta(loading: state.isDeciding))
                .disabled(state.isDeciding)
                .frame(maxWidth: .infinity)
            }
        }
        .padding(MageRideSpacing.md)
    }

    /// The three badges D2' pins above the fare.
    ///
    /// **Third-party booking** is P-05 — the booker is not the rider, which changes who the driver
    /// calls and who is standing at the pickup. **Package · S/M/L** is US-20.3, and it is shown even
    /// when the vehicle is compatible, because P-11 filters candidates but never overrides a driver's
    /// own judgement about what will fit. **Directional** is DT-08's *"the filter is working"* signal.
    @ViewBuilder
    private var badges: some View {
        // A wrapping row: three badges in Sinhala do not fit one line on a 4.7" handset, and a
        // truncated *"Third-party booking"* is the one badge whose meaning is the whole word.
        FlowRow(spacing: MageRideSpacing.xxs) {
            if state.detail?.kind == RideKind.proxy || state.offer?.isProxy == true {
                SolidBadge(label: "offer_badge_proxy".localised, accent: MageRideColor.secondary)
            }
            if let size = state.detail?.packageSize ?? state.offer?.packageSize {
                SolidBadge(
                    label: "offer_badge_package".localisedFormat(PackageLabels.size(size).localised),
                    accent: MageRideVehicleColor.truck
                )
            }
            if isDirectional || state.offer?.directionalMatched == true {
                SolidBadge(label: "offer_badge_directional".localised, accent: MageRideColor.primary)
            }
        }
    }

    /// One of the wireframe's two `kv` rows — `● Pickup` and `◆ Drop`.
    private func placeRow(labelKey: String, symbolName: String, value: String?) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
            Image(systemName: symbolName)
                .font(.caption2)
                .foregroundStyle(MageRideOfferColor.muted)
            Text(key: labelKey)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideOfferColor.muted)
            Spacer(minLength: MageRideSpacing.xs)
            Text(value ?? MageRideSymbols.unknown)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideOfferColor.onOffer)
                .multilineTextAlignment(.trailing)
        }
        .accessibilityElement(children: .combine)
    }

    /// The endings that need a word before the driver is put back on the standby map.
    private func outcomeCard(_ messageKey: String) -> some View {
        VStack(spacing: MageRideSpacing.md) {
            Text(key: messageKey)
                .mageFont(.title)
                .foregroundStyle(MageRideOfferColor.onOffer)
                .multilineTextAlignment(.center)

            Button { onFinished(nil) } label: {
                Text(key: "action_dismiss")
            }
            .buttonStyle(.mageCta)
        }
        .padding(MageRideSpacing.lg)
    }

    /// Why the takeover is closing, when the reason is not simply "time ran out".
    ///
    /// `Taken` and `Expired` are never collapsed — one says somebody was faster and the other says
    /// nobody was, and a driver app that showed "too slow" for a ride nobody took would be lying about
    /// their own acceptance rate.
    static func outcomeMessageKey(_ outcome: OfferOutcome?) -> String? {
        guard let outcome else { return nil }
        switch outcome {
        case is OfferOutcomeTaken: return "offer_taken"
        case is OfferOutcomeWalletBlocked: return "offer_wallet_blocked"
        case is OfferOutcomeFailed: return "error_generic"
        default: return nil
        }
    }

    /// A value that changes exactly when the outcome does.
    ///
    /// `OfferOutcome` is a Kotlin sealed interface, so it is neither `Equatable` nor `Hashable` on this
    /// side of the bridge and `onChange(of:)` cannot watch it directly.
    static func outcomeId(_ outcome: OfferOutcome?) -> String {
        guard let outcome else { return "" }
        switch outcome {
        case let won as OfferOutcomeWon: return "won:" + won.ride.rideId
        case is OfferOutcomeTaken: return "taken"
        case is OfferOutcomeExpired: return "expired"
        case is OfferOutcomeDeclined: return "declined"
        case is OfferOutcomeWalletBlocked: return "wallet"
        case is OfferOutcomeFailed: return "failed"
        default: return ""
        }
    }
}

/// D2' §SCR-DI-014's *"strong haptic on arrival"* and the ride-assigned tone.
///
/// **The audible half is the notification's own sound, and that is a Section C difference worth
/// knowing.** Android plays `RingtoneManager`'s default notification tone from inside the app; iOS
/// exposes no equivalent — an app cannot read, let alone play, the user's chosen tone — so the sound a
/// driver hears is the one on the APNs payload, which ``DriverAppDelegate`` deliberately allows through
/// in the foreground (`[.banner, .sound, .list]`). What is left for the app to do is the *vibration*,
/// and both forms of it are used: the Taptic Engine where there is one, and `kSystemSoundID_Vibrate`
/// where there is not — an iPad, or a handset whose Taptic Engine is off in Accessibility.
///
/// Best-effort, all of it. A driver in a silent profile still gets the screen, which is the part that
/// matters.
enum OfferAlert {

    /// Two buzzes, as close to the Android waveform as this platform expresses.
    ///
    /// `.warning` rather than `.success`: the system's warning pattern is the double tap Android draws
    /// as `400 / 150 / 400`, and it is the one that reads through a jacket pocket at a junction.
    static func announce() {
        let generator = UINotificationFeedbackGenerator()
        generator.prepare()
        generator.notificationOccurred(.warning)
        AudioServicesPlaySystemSound(kSystemSoundID_Vibrate)
    }
}

/// A row that wraps onto the next line when it runs out of width.
///
/// SwiftUI has no `FlowRow` before iOS 16's `Layout` protocol, and this app's deployment target is
/// exactly 16.0 (C085) — so this is the `Layout` implementation rather than a stack of `HStack`s
/// guessing where to break. Three solid badges in Sinhala are wider than a 4.7" screen and the
/// alternative is a truncated *"Third-party booking"*, which is the badge whose whole meaning is the
/// word.
struct FlowRow: Layout {

    var spacing: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) -> CGSize {
        let width = proposal.width ?? .infinity
        let rows = layout(subviews: subviews, width: width)
        let height = rows.reduce(0) { $0 + $1.height } + CGFloat(max(rows.count - 1, 0)) * spacing
        return CGSize(width: proposal.width ?? rows.map(\.width).max() ?? 0, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) {
        var y = bounds.minY
        for row in layout(subviews: subviews, width: bounds.width) {
            // Centred, as the wireframe draws the badge row.
            var x = bounds.minX + (bounds.width - row.width) / 2
            for index in row.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += row.height + spacing
        }
    }

    private struct Row {
        var indices: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    private func layout(subviews: Subviews, width: CGFloat) -> [Row] {
        var rows: [Row] = []
        var current = Row()

        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            let projected = current.indices.isEmpty ? size.width : current.width + spacing + size.width
            if !current.indices.isEmpty, projected > width {
                rows.append(current)
                current = Row()
            }
            current.width = current.indices.isEmpty ? size.width : current.width + spacing + size.width
            current.height = max(current.height, size.height)
            current.indices.append(index)
        }
        if !current.indices.isEmpty { rows.append(current) }
        return rows
    }
}
