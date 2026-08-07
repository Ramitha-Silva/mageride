import MageRideShared
import SwiftUI

/// SCR-PI-015a — *"How do you want to call?"*.
///
/// **There is no masked option and no masking copy anywhere in this file.** AL-48 withdrew the
/// requirement outright: no proxy-DID product exists with +94 numbers, so *"Normal call"* is a
/// **direct cellular dial of the driver's real number**, revealed post-accept in
/// `RideDetail.counterpartyPhone`. The one privacy claim that survives is on the VoIP row, and it is
/// true — an in-app call involves no number at all.
///
/// US-26.5's first-call notice is the transparency that replaced the masking: shown **once**, and
/// only before a direct dial, because warning about number visibility before a VoIP call would be
/// warning about something that is not happening.
///
/// **A `.sheet`, not a destination.** The cell draws it over a scrim with the ride still underneath,
/// and ``PassengerRoute``'s own note lists SCR-PI-015a among the overlays that are deliberately not
/// destinations — a back-stack entry behind something a passenger dismisses with a swipe is a back
/// button that goes somewhere they did not come from.
///
/// **Δ iOS — a `tel:` URL places the call.** Android's `ACTION_DIAL` only opens the dialler, which is
/// why that side needs no guard and this one routes through ``RideContact``; see that protocol.
@MainActor
struct CallChooserSheet: View {

    let driverName: String?
    var driverRating: Double?

    /// The real number. `nil` before acceptance, which greys the direct-dial row rather than hiding
    /// it — a row that vanishes is a control a passenger goes looking for.
    let driverPhone: String?

    let choice: CallChoice
    let contact: RideContact

    /// SCR-PI-028's WebRTC session (C102).
    let onFreeCall: () -> Void

    let onDismiss: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            SheetGrabber()
                .frame(maxWidth: .infinity)

            DriverIdentityRow(name: driverName, rating: driverRating)

            Text(key: "call_how")
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)

            GroupedList {
                CallRow(
                    titleKey: "call_free",
                    // The only honest "number hidden" on this screen: a VoIP call carries no number.
                    captionKey: "call_free_caption",
                    symbolName: "wifi",
                    isHighlighted: choice.remembered == CallType.freeVoip,
                    isEnabled: true
                ) {
                    choice.remember(CallType.freeVoip)
                    onFreeCall()
                }

                CallRow(
                    titleKey: "call_normal",
                    captionKey: "call_normal_caption",
                    symbolName: "phone.fill",
                    isHighlighted: choice.remembered == CallType.directDial,
                    isEnabled: driverPhone != nil,
                    showsSeparator: false
                ) {
                    guard let driverPhone else { return }
                    choice.remember(CallType.directDial)
                    Task {
                        await contact.dial(driverPhone)
                        onDismiss()
                    }
                }
            }

            // US-26.5, once. AL-48 removed masking, so this is the disclosure that replaced it.
            if driverPhone != nil, choice.owesNumberNotice(for: CallType.directDial) {
                Text(key: "call_number_notice")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            OutlinedAction(titleKey: "action_cancel", action: onDismiss)
        }
        .padding(MageRideSpacing.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .presentationDetents([.medium])
        .presentationDragIndicator(.hidden)
    }
}

/// One row of the chooser — the wireframe's `.gr` with a glyph, two lines and a chevron.
///
/// A private view rather than ``GroupedRow`` with a trailing chevron, because the *whole row* is the
/// button here: `GroupedRow` puts its trailing slot outside its own tap target, and a passenger who
/// taps the caption should get the call.
private struct CallRow: View {

    let titleKey: String
    let captionKey: String
    let symbolName: String
    let isHighlighted: Bool
    let isEnabled: Bool
    var showsSeparator = true
    let action: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            Button(action: action) {
                HStack(spacing: MageRideSpacing.sm) {
                    Image(systemName: symbolName)
                        .font(.system(size: MageRideControl.rowIcon))
                        .foregroundStyle(tint)
                        .frame(width: MageRideControl.listRowIcon)

                    VStack(alignment: .leading, spacing: 1) {
                        Text(key: titleKey)
                            .mageFont(.body)
                            .foregroundStyle(tint)
                        Text(key: captionKey)
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                            .multilineTextAlignment(.leading)
                    }

                    Spacer(minLength: MageRideSpacing.xs)

                    Image(systemName: "chevron.right")
                        .font(.system(size: MageRideControl.chipIcon))
                        .foregroundStyle(MageRideColor.outlineVariant)
                }
                .padding(.horizontal, MageRideSpacing.sm)
                .frame(minHeight: MageRideControl.minimumTapTarget + MageRideSpacing.xs)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(!isEnabled)

            if showsSeparator {
                Rectangle()
                    .fill(MageRideColor.surfaceVariant)
                    .frame(height: MageRideControl.hairline)
                    .padding(.leading, MageRideSpacing.sm)
            }
        }
        .overlay {
            // The cell outlines the remembered row (`border:1.5px solid var(--primary)`), which is
            // the same treatment ``SelectionRow`` gives a chosen language.
            if isHighlighted {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(MageRideColor.primary, lineWidth: MageRideControl.selectedBorder)
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isHighlighted ? [.isButton, .isSelected] : .isButton)
    }

    private var tint: Color {
        isEnabled ? MageRideColor.onSurface : MageRideColor.outline
    }
}
