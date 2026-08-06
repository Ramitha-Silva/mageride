import SwiftUI

/// `driver_ios.html`'s status pill, mode badge, step progress bar and vehicle dot, as views.
///
/// The same four shapes appear on three of C087's four screens and again on C088's dashboard, so
/// they belong beside ``FormControls`` rather than inside whichever screen drew one first. Their
/// Android twins are `ui/component/DocumentControls.kt`'s `StatusPill` / `ModeCBadge` and
/// `ui/theme/ControlTokens`; the tones, the tint and the radius are the same on both.

// MARK: - Status pill

/// Which of §0.2's status roles a pill carries.
///
/// `info` is the wireframe's `pill-status.info`, resolved to §0.2's `secondary` — the same role the
/// offline banner uses — rather than to a fourth colour.
enum StatusTone {
    case done
    case pending
    case neutral
    case info

    var accent: Color {
        switch self {
        case .done: return MageRideColor.success
        case .pending: return MageRideColor.warning
        case .neutral: return MageRideColor.onSurfaceVariant
        case .info: return MageRideColor.secondary
        }
    }
}

/// The wireframe's `pill-status` — `Done ✓`, `Approved`, `Incomplete · Step 3 of 4`, `FLEET`.
///
/// **The label is a value, not a key**, because half its uses are formatted (`vehicles_incomplete_step`
/// takes two numbers) and a control that took only keys would push every caller into a second,
/// unformatted shape. Resolve with ``String/localised`` or ``String/localisedFormat(_:)`` before
/// handing it over; nothing here hard-codes copy.
struct StatusPill: View {

    let label: String
    let tone: StatusTone

    var body: some View {
        Text(label)
            .mageFont(.label)
            .foregroundStyle(tone.accent)
            .padding(.horizontal, MageRideSpacing.xs)
            .padding(.vertical, MageRideSpacing.xxs)
            .background(
                tone.accent.opacity(Self.chipTint),
                in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
            )
    }

    /// How much of the accent a tinted chip keeps. Light enough to read its own label over.
    private static let chipTint: Double = 0.18
}

// MARK: - Mode badge

/// The wireframe's `MODE C` badge in the wizard's navigation bar.
///
/// A solid badge in D2' §0.2's mode colour rather than a tinted one: it is an identity, not a
/// status, and it is the one thing on SCR-DI-004 that says which onboarding surface the driver is on
/// — Mode A/B vehicles are the Fleet Portal's (AL-27).
struct ModeCBadge: View {

    var body: some View {
        Text(key: "mode_c_badge")
            .mageFont(.label)
            .foregroundStyle(MageRideColor.onStatus)
            .padding(.horizontal, MageRideSpacing.xs)
            .padding(.vertical, MageRideSpacing.xxs)
            .background(
                MageRideVehicleColor.modeC,
                in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
            )
    }
}

// MARK: - Step progress

/// The wireframe's `.progress` bar with its `Step 2 of 4 · Insurance card or paper` caption.
///
/// A drawn capsule rather than a `ProgressView(value:)`: the wireframe's track is `outlineVariant`
/// and its fill is `primary` at a fixed 6pt, and `ProgressView`'s linear style takes its own height
/// and its own track colour from the platform.
struct StepProgress: View {

    let step: Int
    let count: Int
    let captionKey: String

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            GeometryReader { proxy in
                ZStack(alignment: .leading) {
                    Capsule().fill(MageRideColor.outlineVariant)
                    Capsule()
                        .fill(MageRideColor.primary)
                        .frame(width: proxy.size.width * fraction)
                }
            }
            .frame(height: Self.trackHeight)

            Text("vehicle_onboard_step_of".localisedFormat(step, count, captionKey.localised))
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
        .animation(.easeOut(duration: 0.2), value: step)
        // One announcement: "Step 2 of 4, Insurance card or paper", not a bar and a caption a reader
        // has to assemble (US-19.1/19.2, D2' §C).
        .accessibilityElement(children: .combine)
    }

    private var fraction: CGFloat {
        count > 0 ? CGFloat(step) / CGFloat(count) : 0
    }

    private static let trackHeight: CGFloat = 6
}

// MARK: - Vehicle identity

/// MAP-03's coloured dot beside a vehicle's name — the wireframe's `.cdot`.
struct VehicleTypeDot: View {

    let token: VehicleToken

    var body: some View {
        Circle()
            .fill(token.color)
            .frame(width: MageRideControl.statusDot, height: MageRideControl.statusDot)
            .accessibilityHidden(true)
    }
}

extension StatusPill {

    /// The wireframe's `Done ✓` / `Not captured` pill, which four capture slots draw identically.
    ///
    /// A convenience rather than a fifth control: the two labels and the two tones travel together
    /// everywhere they appear, and spelling them at each call site is how one of them ends up amber.
    static func captured(_ isCaptured: Bool) -> StatusPill {
        StatusPill(
            label: (isCaptured ? "status_done" : "status_not_captured").localised,
            tone: isCaptured ? .done : .neutral
        )
    }
}
