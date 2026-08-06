import SwiftUI

// D2' §0.2 — "iOS: SF Pro with Dynamic Type (mapped roles)". The table maps each MageRide role to
// an iOS text style AND fixes a size and a weight:
//
//   Display  .largeTitle 32/700 · Headline .title 22/700 · Title .title3 18/600
//   Subtitle .headline   16/600 · Body     .body  16/400 · Body small .callout 14/400
//   Label    .caption    12/500 · Caption  .caption2 11/400
//
// **Both halves of that row have to hold at once**, and only one construct does it. A bare
// `Font.largeTitle` honours Dynamic Type and ignores the spec's 32pt (the system default is 34);
// a bare `Font.system(size: 32)` honours the size and does not scale, so a driver who has turned
// text up reads the same small label they could not read before — and US-19.1/19.2 and D2' §C
// ("VoiceOver + Dynamic Type") are the requirement, not a nicety.
//
// `@ScaledMetric(relativeTo:)` is the construct: the spec's point size at the Large content size,
// scaled by the reader's setting on the SAME curve as the mapped text style, re-evaluated from the
// environment so a change while the app is open takes effect without a relaunch.
//
// The face is SF Pro — the system font — so unlike Android there is no bundled binary and, more to
// the point, no gap where Android has one: SF has no Sinhala or Tamil either, and iOS falls back
// per script the same way. A si/ta string renders in the system's Sinhala/Tamil face at these
// sizes and weights. That is the same real deviation from §0.2 that C067 recorded, and it closes
// the same way — a licensed face, which is a design-procurement decision.

/// The eight rows of D2' §0.2's type table.
///
/// One enum rather than eight `Font` constants because on this platform a role is a *pair* — a
/// point size and the Dynamic Type curve it rides — and the pair has to travel together.
enum MageRideTextRole: String, CaseIterable {
    case display
    case headline
    case title
    case subtitle
    case body
    /// The table's "400/500" is one role with an emphasis variant, not two tokens.
    case bodyEmphasis
    case bodySmall
    case label
    case caption

    /// The iOS Dynamic Type style §0.2 maps this role to.
    var textStyle: Font.TextStyle {
        switch self {
        case .display: return .largeTitle
        case .headline: return .title
        case .title: return .title3
        case .subtitle: return .headline
        case .body, .bodyEmphasis: return .body
        case .bodySmall: return .callout
        case .label: return .caption
        case .caption: return .caption2
        }
    }

    /// Point size at the Large content size — the spec's number, before scaling.
    var size: CGFloat {
        switch self {
        case .display: return 32
        case .headline: return 22
        case .title: return 18
        case .subtitle, .body, .bodyEmphasis: return 16
        case .bodySmall: return 14
        case .label: return 12
        case .caption: return 11
        }
    }

    /// The weight, as the spec's CSS-style number.
    var cssWeight: Int {
        switch self {
        case .display, .headline: return 700
        case .title, .subtitle, .label: return 600
        case .bodyEmphasis: return 500
        case .body, .bodySmall, .caption: return 400
        }
    }

    /// The same weight as SwiftUI spells it.
    var weight: Font.Weight {
        switch cssWeight {
        case 700: return .bold
        case 600: return .semibold
        case 500: return .medium
        default: return .regular
        }
    }
}

/// Applies one row of the type table, scaled for the reader's Dynamic Type setting.
private struct MageRideTextRoleModifier: ViewModifier {

    private let role: MageRideTextRole
    @ScaledMetric private var size: CGFloat

    init(role: MageRideTextRole) {
        self.role = role
        _size = ScaledMetric(wrappedValue: role.size, relativeTo: role.textStyle)
    }

    func body(content: Content) -> some View {
        content.font(.system(size: size, weight: role.weight))
    }
}

extension View {

    /// D2' §0.2's type scale. The only way a MageRide view sets a font.
    ///
    /// `.font(.title)` and `.font(.system(size:))` are both wrong here for the reasons in this
    /// file's header; a screen group that reaches for either is either ignoring the spec's sizes
    /// or ignoring the driver's Dynamic Type setting.
    func mageFont(_ role: MageRideTextRole) -> some View {
        modifier(MageRideTextRoleModifier(role: role))
    }
}
