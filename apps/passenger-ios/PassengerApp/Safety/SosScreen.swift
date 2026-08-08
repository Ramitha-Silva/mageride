import MageRideShared
import SwiftUI

/// **SCR-PI-029 · the passenger SOS** (US-12.1, AL-13, D-33, D-34).
///
/// The wireframe on `#2A0A0A`: *"Emergency SOS"*, a 128pt `error` disc inside a translucent halo of
/// itself, the line *"Sending GPS + trip to emergency contacts via SMS…"*, and the contact card
/// carrying *"Amma · +94 77 000 1111"* with a `Sent` pill.
///
/// **A takeover, not a destination.** ``PassengerRoute/sos(rideId:)`` is
/// ``PassengerRoute/isFullScreenTakeover``, so this is presented over the whole app, tab bar
/// included — a passenger on an alarm screen must not be one tap from their trip history, and the
/// cell draws no tab bar.
///
/// **There is nothing to swallow, which is a real Section C difference.** The Android twin disables
/// its `BackHandler` while the request is in flight, because a hardware Back would otherwise pop the
/// screen and clear the view model mid-`POST`. `fullScreenCover` has no interactive dismissal on
/// iOS: the only way out is a button this screen draws, so *"an alarm in flight is not dismissible"*
/// is the presentation's own property here rather than a handler — the same reasoning ``UpdateGate``
/// records for its `.alert`.
///
/// **The footer is the Android twin's, and the cell draws none.** `passenger_ios.html` draws the
/// *dispatched* state only; a screen with no way out of the armed state would be one a mis-tap
/// cannot be taken back from, which is the whole point of the countdown. Layout, controls and states
/// follow the wireframe and behaviour follows Android (the C099 split) — this is the behaviour half.
///
/// - Parameter onFinished: The passenger cancelled before the alarm went, or closed the dispatched
///   state. Closes the takeover.
@MainActor
struct SosScreen: View {

    @StateObject private var model: SosModel

    private let onFinished: () -> Void

    init(
        rideId: String,
        safety: SafetyRepository,
        contacts: SosContacts,
        locations: PassengerLocationSource,
        onFinished: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: SosModel(
                rideId: rideId,
                safety: safety,
                contacts: contacts,
                locations: locations
            )
        )
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

            if let link = model.state.shareLink {
                shareCard(link: link)
            }

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
    /// passenger who pressed it by accident that they still have a moment, and D-33's budget is why
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
        // learns nothing, and a passenger using VoiceOver in an emergency needs the button named.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(key: "sos_title"))
        .accessibilityValue(Text(key: statusKey))
        .accessibilityAddTraits(.isButton)
    }

    /// *"Amma · +94 77 000 1111"* with its `Sent` pill (AL-13, US-12.1).
    ///
    /// D2' §SCR-PI-029 draws the contacts as a list and this app keeps one (SCR-PI-027b's `＋ Add
    /// SOS contact`), so every row is drawn — but **only the primary wears the status pill**,
    /// because D-33's five-second path reads one denormalised number and that is the one the SMS
    /// goes to. Showing `Sent` against three names when one was texted would be the screen inventing
    /// a fan-out the platform does not do.
    ///
    /// A passenger with **no** contact on file is told so here, with the fix named: SCR-PI-027b is
    /// where the list is edited, and an empty list is what makes `POST /v1/sos` answer
    /// `400 no-emergency-contact`.
    private var contactCard: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            if model.state.contacts.isEmpty {
                Text(key: model.state.isContactsLoaded ? "sos_no_contact" : "sos_contacts_loading")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideSosColor.onSos)

                if model.state.warnsNoContact {
                    Text(key: "sos_no_contact_hint")
                        .mageFont(.caption)
                        .foregroundStyle(MageRideSosColor.hint)
                }
            } else {
                // Keyed on the contact rather than on an index pair: **Swift has no key paths into
                // tuples** (the C087 finding), so `Array(_:enumerated())` cannot supply a `ForEach`
                // id here any more than it can anywhere else in this repository.
                ForEach(model.state.contacts, id: \.contactId) { contact in
                    contactRow(contact)
                }
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideSosColor.surface,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .overlay {
            RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                .strokeBorder(MageRideSosColor.outline, lineWidth: 1)
        }
    }

    private func contactRow(_ contact: EmergencyContact) -> some View {
        HStack(spacing: MageRideSpacing.xs) {
            Text(contact.name + MageRideSymbols.separator + contact.phone)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideSosColor.onSos)
                .frame(maxWidth: .infinity, alignment: .leading)

            if contact.contactId == model.state.primaryContact?.contactId, let status = model.state.smsStatus {
                StatusPill(titleKey: Self.smsLabelKey(status), tone: Self.smsTone(status))
            }
        }
        .accessibilityElement(children: .combine)
    }

    /// D-34's live trip link, and the share sheet that sends it (US-12.1).
    ///
    /// The token is the credential and the public view is **live only — there is no replay**, so a
    /// link that leaks stops being useful the moment the trip ends. That is what makes handing one
    /// to somebody over WhatsApp a reasonable thing to offer a passenger in trouble.
    ///
    /// **Δ iOS — `ShareLink`, not a presented controller.** The Android twin builds an `ACTION_SEND`
    /// chooser; SwiftUI has had a first-party share control since iOS 16, which is this target's
    /// floor. The item is the URL **as text**, which is exactly what `type = "text/plain"` sends on
    /// the other side — a `URL` item would let some receivers rewrite it into a title-and-link pair
    /// the recipient cannot paste into a browser.
    private func shareCard(link: String) -> some View {
        VStack(spacing: MageRideSpacing.xxs) {
            Text(key: "sos_share_hint")
                .mageFont(.caption)
                .foregroundStyle(MageRideSosColor.hint)
                .multilineTextAlignment(.center)

            ShareLink(item: link) {
                Text(key: "sos_share_action")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideSosColor.onSos)
                    .frame(minHeight: MageRideControl.minimumTapTarget)
            }
        }
        .frame(maxWidth: .infinity)
    }

    /// Cancel while armed; Close once the alarm has gone; Try again when it never left the handset.
    @ViewBuilder
    private var footer: some View {
        switch model.state.stage {
        case .armed:
            footerButton(key: "action_cancel") {
                model.cancelCountdown()
                onFinished()
            }

        case .sending:
            Text(key: "sos_sending")
                .mageFont(.label)
                .foregroundStyle(MageRideSosColor.hint)
                .frame(minHeight: MageRideControl.minimumTapTarget)

        case .dispatched:
            footerButton(key: "action_close", action: onFinished)

        case .failed:
            HStack(spacing: MageRideSpacing.md) {
                footerButton(key: "action_retry", action: model.retry)
                footerButton(key: "action_close", tint: MageRideSosColor.hint, action: onFinished)
            }
        }
    }

    private func footerButton(
        key: String,
        tint: Color = MageRideSosColor.onSos,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Text(key: key)
                .mageFont(.subtitle)
                .foregroundStyle(tint)
                .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    /// What the disc reads — the countdown while there is one to run, the distress signal otherwise.
    private var discLabel: String {
        guard model.state.stage == .armed, !model.state.isAwaitingPosition else { return SosLabels.sos }
        return String(model.state.secondsLeft)
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
    /// `failed` is **not** an error state on this screen and does not colour like one: the alert is
    /// recorded and is on the admin live feed either way, and telling somebody in trouble that
    /// nothing happened would be worse than telling them the SMS leg did not manage it.
    static func smsLabelKey(_ status: SosSmsStatus) -> String {
        switch status {
        case SosSmsStatus.dispatched: return "sos_sms_sent"
        case SosSmsStatus.failed: return "sos_sms_failed"
        default: return "sos_sms_no_contact"
        }
    }

    static func smsTone(_ status: SosSmsStatus) -> StatusPill.Tone {
        status == SosSmsStatus.dispatched ? .ok : .warning
    }
}

/// The word on the disc, and why it is not copy.
///
/// `SOS` is an international distress signal, not a sentence: it is the same three letters in
/// Sinhala, Tamil and English, and three identical values in the three `Localizable.strings` files is
/// exactly what `LocalizationTests.testNoTranslationWasLeftAsItsEnglishPlaceholder` reads as a key
/// nobody translated. Same rule as `Rs` (``MoneyFormat/prefix``), `+94` (``PhoneNumber``) and the
/// language endonyms, and the same constant as
/// `apps/passenger-android/.../safety/SosScreen.kt`'s `SosLabels`. The **title** above it is
/// ordinary copy and is translated, which is why `ride_sos` on SCR-PI-015 reads *"හදිසි උදව්"* and
/// this does not.
enum SosLabels {
    static let sos = "SOS"
}
