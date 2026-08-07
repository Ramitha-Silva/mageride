import MageRideShared
import SwiftUI

/// **SCR-DI-032 · the driver SOS** (US-12.8, AL-13, D-33).
///
/// The wireframe on `#2A0A0A`: *"Driver SOS"*, a 128pt `error` disc inside a translucent halo of
/// itself, the line *"Sending GPS + active trip via SMS…"*, and the contact card carrying
/// *"Amma · +94 77 000 1111"* with a `Sent` pill.
///
/// **A takeover, not a destination.** ``DriverRoute/sos(rideId:)`` is
/// ``DriverRoute/isFullScreenTakeover``, so this is presented over the whole app, tab bar included —
/// a driver on an alarm screen must not be one tap from their wallet.
///
/// **There is nothing to swallow, which is a real Section C difference.** The Android twin disables
/// its `BackHandler` while the request is in flight, because a hardware Back would otherwise pop the
/// screen and clear the view model mid-`POST`. `fullScreenCover` has no interactive dismissal on
/// iOS: the only way out is the button this screen draws, so *"an alarm in flight is not
/// dismissible"* is the presentation's own property here rather than a handler. The same reasoning
/// ``UpdateGate`` records for its `.alert`.
///
/// - Parameter onFinished: The driver cancelled before the alarm went, or closed the dispatched
///   state. Closes the takeover.
@MainActor
struct SosScreen: View {

    @StateObject private var model: SosModel

    private let onFinished: () -> Void

    init(model: @autoclosure @escaping () -> SosModel, onFinished: @escaping () -> Void) {
        _model = StateObject(wrappedValue: model())
        self.onFinished = onFinished
    }

    var body: some View {
        VStack(spacing: MageRideSpacing.md) {
            Spacer(minLength: 0)

            Text(key: "sos_title")
                .mageFont(.title)
                .foregroundStyle(MageRideSosColor.onSos)

            disc

            Text(key: statusKey)
                .mageFont(.label)
                .foregroundStyle(model.state.stage == .failed ? MageRideColor.error : MageRideSosColor.hint)
                .multilineTextAlignment(.center)

            if let errorKey = model.state.errorKey {
                Text(key: errorKey)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.error)
                    .multilineTextAlignment(.center)
            }

            contactCard

            Spacer(minLength: 0)

            footer
        }
        .padding(MageRideSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideSosColor.background)
        .task { model.start() }
        .onDisappear(perform: model.stop)
    }

    /// The wireframe's disc, and the only control that raises the alarm.
    ///
    /// While armed it carries the **countdown** rather than the word: the number is what tells a
    /// driver who pressed it by accident that they still have a moment, and D-33's budget is why
    /// that moment is three seconds and not ten (see ``SosModel/countdownSeconds``).
    private var disc: some View {
        ZStack {
            Circle()
                .fill(MageRideSosColor.halo)
                .frame(
                    width: MageRideControl.sosButton + MageRideControl.sosHalo * 2,
                    height: MageRideControl.sosButton + MageRideControl.sosHalo * 2
                )

            Button(action: model.raise) {
                Text(discLabel)
                    .mageFont(.headline)
                    .foregroundStyle(MageRideColor.onStatus)
                    .frame(width: MageRideControl.sosButton, height: MageRideControl.sosButton)
                    .background(MageRideColor.error, in: Circle())
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .disabled(model.state.stage != .armed)
        }
        // One announcement, and it is the *action* rather than the number: a reader hearing "3"
        // learns nothing, and a driver using VoiceOver in an emergency needs the button named.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(key: "sos_title"))
        .accessibilityValue(Text(key: statusKey))
        .accessibilityAddTraits(.isButton)
    }

    /// *"Amma · +94 77 000 1111"* with its `Sent` pill (AL-13).
    ///
    /// A driver with **no** contact on file is told so here rather than being refused the alarm: the
    /// event is still recorded and still reaches the admin live feed, and safety-svc answers
    /// `SosSmsStatus.noContact` for the leg that has nowhere to go. The fix is SCR-DI-029, which is
    /// where the contact is set.
    private var contactCard: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Text(contactLabel)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideSosColor.onSos)
                .frame(maxWidth: .infinity, alignment: .leading)

            if let status = model.state.smsStatus {
                StatusPill(label: Self.smsLabelKey(status).localised, tone: Self.smsTone(status))
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity)
        .background(
            MageRideSosColor.surface,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .overlay {
            RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                .strokeBorder(MageRideSosColor.outline, lineWidth: 1)
        }
        .accessibilityElement(children: .combine)
    }

    /// Cancel while armed; Close once the alarm has gone; Try again when it never left the handset.
    @ViewBuilder
    private var footer: some View {
        switch model.state.stage {
        case .armed:
            Button {
                model.cancelCountdown()
                onFinished()
            } label: {
                Text(key: "action_cancel")
                    .mageFont(.subtitle)
                    .foregroundStyle(MageRideSosColor.onSos)
                    .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
            }
            .buttonStyle(.plain)

        case .sending:
            Text(key: "sos_sending")
                .mageFont(.label)
                .foregroundStyle(MageRideSosColor.hint)
                .frame(minHeight: MageRideControl.minimumTapTarget)

        case .dispatched:
            Button(action: onFinished) {
                Text(key: "action_close")
                    .mageFont(.subtitle)
                    .foregroundStyle(MageRideSosColor.onSos)
                    .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
            }
            .buttonStyle(.plain)

        case .failed:
            HStack(spacing: MageRideSpacing.md) {
                Button(action: model.retry) {
                    Text(key: "action_retry")
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideSosColor.onSos)
                        .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
                }
                .buttonStyle(.plain)

                Button(action: onFinished) {
                    Text(key: "action_close")
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideSosColor.hint)
                        .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
                }
                .buttonStyle(.plain)
            }
        }
    }

    /// What the disc reads — the countdown while there is one to run, the distress signal otherwise.
    private var discLabel: String {
        guard model.state.stage == .armed, !model.state.isAwaitingPosition else { return SosLabels.sos }
        return String(model.state.secondsLeft)
    }

    /// *"Amma · +94 77 000 1111"*. The number is a proper noun and the dot is ``MageRideSymbols``'.
    private var contactLabel: String {
        guard let contact = model.state.contact else { return "sos_no_contact".localised }
        return contact.name + MageRideSymbols.separator + contact.phone
    }

    /// The line under the disc. Four states, four sentences.
    private var statusKey: String {
        switch model.state.stage {
        case .armed: return model.state.isAwaitingPosition ? "sos_waiting_position" : "sos_armed"
        case .sending: return "sos_sending_body"
        case .dispatched: return "sos_dispatched"
        case .failed: return "sos_failed"
        }
    }

    /// What the pill says about D-33's parallel gateways.
    ///
    /// `Failed` is **not** an error state on this screen and does not colour like one: the alert is
    /// recorded and is on the admin live feed either way, and telling somebody in trouble that
    /// nothing happened would be worse than telling them the SMS leg did not manage it.
    static func smsLabelKey(_ status: SosSmsStatus) -> String {
        switch status {
        case SosSmsStatus.dispatched: return "sos_sms_sent"
        case SosSmsStatus.failed: return "sos_sms_failed"
        default: return "sos_sms_no_contact"
        }
    }

    static func smsTone(_ status: SosSmsStatus) -> StatusTone {
        status == SosSmsStatus.dispatched ? .done : .pending
    }
}

/// The word on the disc, and why it is not copy.
///
/// `SOS` is an international distress signal, not a sentence: it is the same three letters in
/// Sinhala, Tamil and English, and three identical values in the three `Localizable.strings` files is
/// exactly what `LocalizationTests.testNoTranslationWasLeftAsItsEnglishPlaceholder` reads as a key
/// nobody translated. Same rule as `Rs`, `L3` and `+94` (C086), and the same constant as
/// `apps/driver-android/.../safety/SosScreen.kt`'s `SosLabels`. The **title** above it is ordinary
/// copy and is translated.
enum SosLabels {
    static let sos = "SOS"
}
