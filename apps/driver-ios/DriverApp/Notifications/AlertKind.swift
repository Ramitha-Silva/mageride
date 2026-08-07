import SwiftUI

/// The alert kinds SCR-DI-034 draws, and what each one looks like.
///
/// D2' §SCR-DI-034's own list — *"dispatch/fee/registration/sharing/directional"* — with the push
/// types the wireframe names under it: `RIDE_OFFER`, `DIRECTIONAL_EXPIRING`, `LOW_BALANCE`,
/// `TOPUP_CONFIRMED`, `SHARE_REQUEST`, `package_*`, `SOS_*`.
///
/// **Matched on the wire value, and an unmatched one still draws.** The type is `data.kind`, which is
/// notification-svc's catalogue name and *"grows without a contract change"* — the same reason
/// SCR-DI-029's notification switches are a map rather than an enum (C092). A kind this build has
/// never heard of gets the neutral bell and the push's own title, because a driver being shown
/// nothing is worse than being shown a row with a generic icon.
///
/// - Parameters:
///   - tone: Which of the four status colours tints the row's leading square.
///   - symbolName: The wireframe's glyph, as SF Symbols.
///   - labelKey: What the row says when the push carried no title of its own.
enum AlertKind: CaseIterable {

    /// E-01's fifteen-second offer (`ride_offer`).
    case rideOffer

    /// DT-08's ten-minute warning (`DIRECTIONAL_EXPIRING`, US-10.14).
    case directional

    /// US-9.9's `LOW_BALANCE`, once below Rs 200.
    case lowBalance

    /// `TOPUP_CONFIRMED` / `PAYMENT_CONFIRMED` (US-8.12).
    case moneyIn

    /// A Mode B access request (`SHARE_REQUEST`, US-10.2).
    case share

    /// `package_picked` / `package_delivered` (US-10.12/13).
    case package

    /// `SOS_TRIGGERED` / `SOS_RESOLVED` — the types US-10.7 does not let a driver mute.
    case safety

    /// Everything else notification-svc sends.
    case other

    var tone: StatusTone {
        switch self {
        case .rideOffer, .directional, .other: return .neutral
        case .lowBalance, .safety: return .pending
        case .moneyIn: return .done
        case .share, .package: return .info
        }
    }

    var symbolName: String {
        switch self {
        case .rideOffer: return "car.fill"
        case .directional: return "arrow.right"
        case .lowBalance: return "arrow.down.circle.fill"
        case .moneyIn: return "checkmark.circle.fill"
        case .share: return "link"
        case .package: return "shippingbox.fill"
        case .safety: return "shield.lefthalf.filled"
        case .other: return "bell.fill"
        }
    }

    var labelKey: String {
        switch self {
        case .rideOffer: return "alert_kind_ride_offer"
        case .directional: return "alert_kind_directional"
        case .lowBalance: return "alert_kind_low_balance"
        case .moneyIn: return "alert_kind_money_in"
        case .share: return "alert_kind_share"
        case .package: return "alert_kind_package"
        case .safety: return "alert_kind_safety"
        case .other: return "alert_kind_other"
        }
    }

    /// Resolves a stored `type`.
    ///
    /// Case-insensitive and prefix-matched on the two families the catalogue spells with a suffix
    /// (`package_*`, `SOS_*`), which is how D5' §14.4 and the wireframe both write them. The same
    /// table as `apps/driver-android/.../notifications/AlertKind.kt`'s `of`.
    static func of(_ type: String) -> AlertKind {
        let key = type.uppercased()
        if key == "RIDE_OFFER" { return .rideOffer }
        if key.hasPrefix("DIRECTIONAL") { return .directional }
        if key == "LOW_BALANCE" { return .lowBalance }
        if key == "TOPUP_CONFIRMED" || key == "PAYMENT_CONFIRMED" { return .moneyIn }
        if key.hasPrefix("SHARE") { return .share }
        if key.hasPrefix("PACKAGE") { return .package }
        if key.hasPrefix("SOS") { return .safety }
        return .other
    }

    /// The colour of an alert's leading square.
    ///
    /// Resolved from ``StatusTone`` rather than from a fourth palette, because D2' §0.2 has exactly
    /// these roles and the wireframe's own row tints (`--primary`, `--error`, `--iosGreen`,
    /// `--secondary`) are the same four ideas — a dispatch row, a money-out row, a money-in row and
    /// a sharing row.
    ///
    /// One deliberate substitution: the wireframe's `--iosGreen` is the platform's system green, and
    /// §0.2's `success` is MageRide's. §0.2 is authoritative for a *semantic* role and the platform's
    /// own colour is kept only where the platform draws the control itself (the `Toggle`, the
    /// `.alert` actions) — a tinted square this app draws is ours.
    var accent: Color { tone.accent }
}
