import SwiftUI

/// **SCR-PI-003 · phone + OTP** — US-1.1/1.10.
///
/// The wireframe draws **both halves on one screen**: a `‹ Back` nav row, the large title *Mobile
/// number*, one sentence, the `+94` field, an `enter code` divider, six OTP boxes, the
/// *"Didn't get it? Resend (54s)"* row, a spacer and `Continue`. Which half is *live* is
/// ``LoginPhase`` — the code boxes are disabled until a code is out, and the number is disabled once
/// one is.
///
/// **Errors go in an `.alert`, and that is the cell's own `Δ iOS` clause** (*"errors via `.alert`;
/// success `.notification(.success)`"*), where the Android screen puts them inline. Both are
/// resolved copy from ``OnboardingErrors`` and never a `ProblemDetails` string (D-26). The one thing
/// that stays inline is the attempts counter, which is a *state* of the attempt rather than an
/// event — see below.
@MainActor
struct LoginScreen: View {

    @StateObject private var model: LoginModel

    private let onSignedIn: (PassengerDestination) -> Void

    init(
        sessions: PassengerSessions,
        onboarding: OnboardingRepository,
        profiles: PassengerProfileRepository,
        preferences: AppPreferences,
        pushTokens: PushTokenProvider,
        onSignedIn: @escaping (PassengerDestination) -> Void
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
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            back

            Text(key: "login_title")
                .mageFont(.display)
                .foregroundStyle(MageRideColor.onSurface)

            Text(key: "login_subtitle")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            PhoneNumberField(
                value: phoneBinding,
                isEnabled: model.state.phase == .phone && !model.state.isBusy
            )

            LabelledDivider(key: "login_enter_code")

            OtpField(
                value: otpBinding,
                length: LoginState.otpLength,
                isEnabled: model.state.phase == .otp && !model.state.isBusy,
                isError: model.state.errorKey != nil
            )

            resend

            if let remaining = model.state.attemptsRemaining, model.state.phase == .otp {
                // **Information, not a failure**, so it is not `error`-coloured and it is not in the
                // alert: how many tries are left is a standing fact about the attempt, and an alert
                // that had to be dismissed to see the boxes again would be in the way of the one
                // thing the passenger is trying to do. It is what stops somebody burning the last
                // attempt guessing.
                Text("login_attempts_remaining".localisedFormat(remaining))
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            // Everything above sits at the top; Continue sits at the bottom, as the wireframe's
            // `.spacer` before the `.cta` draws it.
            Spacer(minLength: MageRideSpacing.md)

            Button {
                Task { await model.submit() }
            } label: {
                Text(key: "action_continue")
            }
            .buttonStyle(.mageCta(loading: model.state.isBusy))
            .disabled(!model.state.canSubmit)
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .background(MageRideColor.surface)
        .task { await model.start() }
        .onChange(of: model.destination) { destination in
            if let destination {
                // The cell's own clause: `.notification(.success)` on a verify that took. It fires
                // here rather than in the model because a haptic is a rendering, and a model that
                // reached for `UINotificationFeedbackGenerator` could not be tested without one.
                UINotificationFeedbackGenerator().notificationOccurred(.success)
                onSignedIn(destination)
            }
        }
        .alert(
            Text(key: "login_error_title"),
            isPresented: alertBinding,
            presenting: model.state.errorKey
        ) { _ in
            Button { } label: { Text(key: "action_ok") }
        } message: { key in
            Text(key: key)
        }
        // The alert is the cell's own `Δ iOS` clause and it is the only place a server failure is
        // rendered on this screen. `FormErrorText` exists for a field-level message and cluster 1
        // has none — C096 onwards will.
    }

    /// The wireframe's `‹ Back`.
    ///
    /// **It goes back a *step*, not a screen.** From the code half it returns to the number and
    /// cancels the attempt server-side (``LoginModel/editPhoneNumber()``); from the number half
    /// there is nowhere to go — cluster 1 is a one-way flow and the shell replaces its root rather
    /// than stacking, so there is no onboarding screen behind this one to return to. Hidden rather
    /// than disabled, because a control that is visible and inert is a promise the screen does not
    /// keep.
    @ViewBuilder
    private var back: some View {
        if model.state.phase == .otp {
            HStack {
                TextLink(key: "action_back", isEnabled: !model.state.isBusy) {
                    Task { await model.editPhoneNumber() }
                }
                Spacer()
            }
        }
    }

    /// *"Didn't get it? Resend (54s)"*.
    ///
    /// The countdown is **shown** rather than the button merely disabled: a bare inert "Resend"
    /// tells a passenger nothing and they tap it until D-32 locks them out for an hour. See
    /// ``LoginState/canResend``.
    @ViewBuilder
    private var resend: some View {
        if model.state.phase == .otp {
            HStack(spacing: MageRideSpacing.xs) {
                Text(key: "login_didnt_get_it")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                if model.state.resendInSeconds > 0 {
                    CountdownLink(
                        key: "login_resend_in",
                        seconds: model.state.resendInSeconds,
                        isEnabled: false
                    ) { }
                } else {
                    TextLink(key: "login_resend", isEnabled: model.state.canResend) {
                        Task { await model.resend() }
                    }
                }

                Spacer()
            }
        }
    }

    private var phoneBinding: Binding<String> {
        Binding(get: { model.state.phone }, set: { model.onPhoneChanged($0) })
    }

    private var otpBinding: Binding<String> {
        Binding(get: { model.state.otp }, set: { model.onOtpChanged($0) })
    }

    /// `.constant`-style presentation over a value the model owns.
    ///
    /// The setter clears the error rather than the alert: SwiftUI dismisses the alert when the
    /// binding goes false, and the error going away is what *makes* it false. Routing it through the
    /// model is what keeps one source of truth for "is something wrong".
    private var alertBinding: Binding<Bool> {
        Binding(
            get: { model.state.errorKey != nil },
            set: { presented in if !presented { model.dismissError() } }
        )
    }
}
