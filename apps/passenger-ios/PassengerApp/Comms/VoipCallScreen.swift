import SwiftUI

/// **SCR-PI-028 · the in-app VoIP call** (US-6A.16, D-24, AL-48).
///
/// The wireframe, top to bottom on a dark screen: a caption, a large avatar disc, the driver's name,
/// the state line, a spacer, the 🔇 / 🔊 pair with a small label under each, and a red hang-up disc.
///
/// **Dark in both appearances** — see ``MageRideCallColor``. A call screen that turned white in the
/// light theme would be a different screen twice a day, and a phone call has one appearance.
///
/// **A takeover, not a destination** (``PassengerRoute/isFullScreenTakeover``), so there is no back
/// button and no tab bar. Leaving is the red disc — which hangs up, reports the outcome and *then*
/// closes — or the system's own End on the lock screen, which ``CallKitSession`` routes to the same
/// method. `fullScreenCover` has no interactive dismissal on iOS, so unlike the Android twin there
/// is no Back gesture to intercept: a room cannot be left joined behind a screen the passenger
/// swiped away, and `BackHandler(enabled:)` has no counterpart to write.
///
/// **Two places this cell and `passenger_android.html` differ, and both are drawn the iOS way.** The
/// cell puts a **label under each `.fab`** (`mute`, `speaker`) where the Android cell draws the two
/// discs bare, and its hang-up disc is 62pt against the Android 64 — the same two numbers
/// `driver_ios.html` draws. What is **not** taken from the cell is its call palette: see
/// ``MageRideCallColor`` for why the transcribed one wins over the CSS.
///
/// - Parameter onFinished: The call is over — hung up, dialled normally, or ended by the system.
@MainActor
struct VoipCallScreen: View {

    @StateObject private var model: VoipCallModel

    private let onFinished: () -> Void

    init(
        rideId: String,
        rides: RideRepository,
        contact: RideContact,
        engine: VoipEngine,
        session: CallSession,
        onFinished: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: VoipCallModel(
                rideId: rideId,
                rides: rides,
                contact: contact,
                engine: engine,
                session: session
            )
        )
        self.onFinished = onFinished
    }

    var body: some View {
        VStack(spacing: MageRideSpacing.md) {
            Spacer(minLength: MageRideSpacing.xxl)

            // "number hidden" is a PROPERTY of a VoIP call, not a promise the platform makes any
            // more: AL-48 withdrew masking, and the caption is true only because a LiveKit room
            // carries no MSISDN — which is also why ``CallKitSession`` reports a `.generic` handle
            // rather than a phone number. It is deliberately absent from the failure state, where
            // the very next thing offered is a dial of the driver's real number.
            if model.state.isLive {
                Text(key: "call_in_app")
                    .mageFont(.label)
                    .foregroundStyle(MageRideCallColor.hint)
            }

            avatar

            Text(calleeName)
                .mageFont(.headline)
                .foregroundStyle(MageRideCallColor.onCall)
                .multilineTextAlignment(.center)

            statusLine

            Spacer(minLength: 0)

            if let errorKey = model.state.errorKey {
                Text(key: errorKey)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.error)
                    .multilineTextAlignment(.center)
                    .onTapGesture(perform: model.consume)
            }

            if model.state.canDialDirectly {
                directDialPrompt
            }

            actions

            Spacer(minLength: MageRideSpacing.lg)
        }
        .padding(MageRideSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideCallColor.background)
        .task { model.start() }
        .onChange(of: model.state.isFinished) { finished in
            if finished { onFinished() }
        }
    }

    /// The wireframe's `.avatar lg` — a plain disc, because no photo of a driver reaches this app.
    private var avatar: some View {
        Image(systemName: "person.fill")
            .font(.system(size: MageRideControl.illustrationIcon))
            .foregroundStyle(MageRideCallColor.hint)
            .frame(width: MageRideControl.avatarLarge, height: MageRideControl.avatarLarge)
            .background(MageRideCallColor.surface, in: Circle())
            .accessibilityHidden(true)
    }

    /// The one line that says where the call is — *"Connecting…"*, *"Connected · 01:24"* or the
    /// failure.
    ///
    /// The failure copy is chosen by ``VoipFailure`` rather than being one sentence, because the
    /// three causes need different advice: a ride that has ended has nobody to dial, and a build
    /// with no media client is not a network problem the passenger can wait out.
    @ViewBuilder
    private var statusLine: some View {
        switch model.state.stage {
        case .connecting:
            Text(key: "call_connecting")
                .mageFont(.label)
                .foregroundStyle(MageRideCallColor.connected)

        case .connected:
            Text("call_connected".localised + MageRideSymbols.separator + MoneyFormat.timer(seconds: model.state.seconds))
                .mageFont(.label)
                .foregroundStyle(MageRideCallColor.connected)
                // Announced as it changes rather than per second: a reader that spoke every tick
                // would talk over the call it is describing.
                .accessibilityLabel(Text(key: "call_connected"))

        case .failed:
            Text(key: Self.failureKey(model.state.failure))
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.error)
                .multilineTextAlignment(.center)
        }
    }

    /// **AL-48's *"Call normally instead?"*** — the whole of what a VoIP failure falls back to.
    ///
    /// A direct cellular dial of the driver's real number. The masked-number bridge and D-25's
    /// masked-SMS relay were both withdrawn, so there is no third option to offer and none is drawn
    /// — and no copy on this screen claims the passenger's number stays hidden once they take it.
    private var directDialPrompt: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Text(key: "call_fallback_question")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideCallColor.hint)
                .multilineTextAlignment(.center)

            Button(action: model.dialDirectly) {
                Text(key: "call_fallback_action")
            }
            .buttonStyle(.mageCta)
        }
        .frame(maxWidth: .infinity)
    }

    /// The wireframe's two `.fab`s with their captions, and its red disc. Mute and speaker are gone
    /// once a call has failed — there is no audio to mute.
    private var actions: some View {
        VStack(spacing: MageRideSpacing.md) {
            if model.state.isLive {
                // `space-around` in the cell's own CSS, which is a full-width row rather than a
                // spaced pair.
                HStack {
                    Spacer(minLength: 0)
                    toggle(
                        symbolName: model.state.isMuted ? "mic.slash.fill" : "mic.fill",
                        labelKey: model.state.isMuted ? "call_unmute" : "call_mute",
                        isActive: model.state.isMuted,
                        action: model.toggleMute
                    )
                    Spacer(minLength: 0)
                    toggle(
                        symbolName: model.state.isSpeakerOn ? "speaker.wave.2.fill" : "speaker.fill",
                        labelKey: "call_speaker",
                        isActive: model.state.isSpeakerOn,
                        action: model.toggleSpeaker
                    )
                    Spacer(minLength: 0)
                }
            }

            Button(action: model.hangUp) {
                Image(systemName: "phone.down.fill")
                    .font(.title2)
                    .foregroundStyle(MageRideColor.onStatus)
                    .frame(width: MageRideControl.callEnd, height: MageRideControl.callEnd)
                    .background(MageRideColor.error, in: Circle())
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text(key: model.state.isLive ? "call_hang_up" : "action_close"))
        }
    }

    /// One of the wireframe's `.fab` controls — a 44pt disc that reads as pressed when it is on,
    /// with the cell's own 10px caption underneath it.
    ///
    /// The caption is the same key VoiceOver reads, so the two cannot drift: a label that said
    /// *"speaker"* while the reader said something else is the accessibility bug this shape is
    /// easiest to introduce.
    private func toggle(
        symbolName: String,
        labelKey: String,
        isActive: Bool,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            VStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: symbolName)
                    .font(.body)
                    .foregroundStyle(isActive ? MageRideCallColor.background : MageRideCallColor.onCall)
                    .frame(width: MageRideControl.callAction, height: MageRideControl.callAction)
                    .background(isActive ? MageRideCallColor.onCall : MageRideCallColor.surface, in: Circle())

                Text(key: labelKey)
                    .mageFont(.caption)
                    .foregroundStyle(MageRideCallColor.hint)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(key: labelKey))
        .accessibilityAddTraits(isActive ? [.isButton, .isSelected] : .isButton)
    }

    /// *"K. Fernando"*, or the role alone before the ride has been read.
    private var calleeName: String {
        guard let name = model.state.calleeName, !name.isEmpty else { return "call_driver".localised }
        return name
    }

    /// The copy for a failure, by cause.
    static func failureKey(_ failure: VoipFailure?) -> String {
        switch failure {
        case .signalling: return "call_failed_signalling"
        case .media: return "call_failed_media"
        // Not a network condition — this build carries no WebRTC client at all. See
        // ``AbsentVoipEngine``.
        case .noMediaClient, nil: return "call_failed_unavailable"
        }
    }
}
