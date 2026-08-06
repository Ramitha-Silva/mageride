import SwiftUI

/// **SCR-DI-003 · phone + OTP** — the whole of sign-in, on one screen.
///
/// The wireframe draws the number field and the six code cells together, separated by an "enter
/// code" divider, with the resend countdown under them and one Continue CTA at the bottom. That is
/// what this is: the code half is disabled until a code has been sent, and the CTA changes which
/// half it submits rather than the screen changing.
///
/// *"Phone-OTP only · no Google Sign-In (US-11.5)"* is on the screen because it is a promise to the
/// driver, not a note to us: there is no other button, and there never will be one here.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct LoginScreen: View {

    @StateObject private var model: LoginModel

    private let onSignedIn: (OnboardingDestination) -> Void

    init(
        sessions: DriverSessions,
        onboarding: OnboardingRepository,
        profiles: DriverProfileRepository,
        preferences: OnboardingPreferences,
        pushTokens: PushTokenProvider,
        onSignedIn: @escaping (OnboardingDestination) -> Void
    ) {
        _model = StateObject(
            wrappedValue: LoginModel(
                sessions: sessions,
                onboarding: onboarding,
                profiles: profiles,
                preferences: preferences,
                pushTokens: pushTokens
            )
        )
        self.onSignedIn = onSignedIn
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                    Text(key: "login_phone_otp_only")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)

                    PhoneNumberField(
                        value: Binding(get: { model.state.phone }, set: model.onPhoneChanged),
                        isEnabled: model.state.phase == .phone && !model.state.isBusy,
                        isError: model.state.errorKey != nil && model.state.phase == .phone
                    )

                    otpSection

                    if let errorKey = model.state.errorKey {
                        FormErrorText(messageKey: errorKey)
                    }

                    Button(action: { Task { await model.submit() } }) {
                        Text(key: "action_continue")
                    }
                    .buttonStyle(.mageCta(loading: model.state.isBusy))
                    .disabled(!model.state.canSubmit)
                    .padding(.top, MageRideSpacing.xs)
                }
                .padding(MageRideSpacing.md)
            }
            .background(MageRideColor.background)
            .navigationTitle(Text(key: "login_title"))
            .navigationBarTitleDisplayMode(.large)
            .toolbar { backButton }
        }
        .task { await model.start() }
        .onChange(of: model.destination) { destination in
            if let destination { onSignedIn(destination) }
        }
    }

    /// The wireframe's `‹ Back`, and the one thing it can do here.
    ///
    /// **Δ Section C.** Android pops the back stack; from the code half that returns to the number,
    /// and from the number half there is nothing behind it (`replaceOnboarding` pops the whole
    /// graph on every cluster-1 step) so it leaves the app. iOS has no "leave the app" and the
    /// pre-session flow is a replaced root rather than a stack, so the control is drawn where it has
    /// somewhere to go — which is the state the wireframe itself draws it in, with two digits typed
    /// and the resend counting down.
    @ToolbarContentBuilder
    private var backButton: some ToolbarContent {
        // `.navigationBarLeading` rather than `.topBarLeading`: the latter is iOS 17 and this
        // target's floor is 16.0 (`Config/Shared.xcconfig`).
        ToolbarItem(placement: .navigationBarLeading) {
            if model.state.phase == .otp {
                Button(action: { Task { await model.editPhoneNumber() } }) {
                    Label {
                        Text(key: "action_back")
                    } icon: {
                        Image(systemName: "chevron.left")
                    }
                }
                .disabled(model.state.isBusy)
                .tint(MageRideColor.primary)
            }
        }
    }

    /// The wireframe's "enter code" divider, the six cells and the resend row.
    ///
    /// The resend is refused locally while the countdown runs. D-32 gives the number five OTPs an
    /// hour and a 60-second bucket between them, so a tap inside the window would spend one of the
    /// five on a message the server was never going to send.
    private var otpSection: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            HStack(spacing: MageRideSpacing.xs) {
                dividerLine
                Text(key: "login_enter_code")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                dividerLine
            }

            OtpField(
                value: Binding(get: { model.state.otp }, set: model.onOtpChanged),
                length: LoginState.otpLength,
                isEnabled: model.state.phase == .otp && !model.state.isBusy,
                isError: model.state.errorKey != nil && model.state.phase == .otp
            )

            HStack {
                Text(key: "login_resend_code")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Spacer(minLength: MageRideSpacing.xs)
                if model.state.canResend {
                    Button(action: { Task { await model.resend() } }) {
                        Text(key: "login_resend_action")
                            .mageFont(.bodyEmphasis)
                            .foregroundStyle(MageRideColor.primary)
                    }
                } else {
                    Text("login_resend_in".localisedFormat(model.state.resendInSeconds))
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.outlineVariant)
                }
            }

            if let attempts = model.state.attemptsRemaining {
                Text("login_attempts_remaining".localisedFormat(attempts))
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
        }
        .opacity(model.state.phase == .otp ? 1 : 0.55)
    }

    private var dividerLine: some View {
        Rectangle()
            .fill(MageRideColor.outline)
            .frame(height: MageRideControl.hairline)
    }
}
