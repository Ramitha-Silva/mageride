import SwiftUI

/// `passenger_ios.html`'s cluster-5 shapes — the four-step handoff bar and the handover-code card.
///
/// Same rule as ``FormControls``, ``OnboardingControls``, ``MapControls``, ``BookingControls`` and
/// ``RideControls``: every measurement comes from ``MageRideSpacing`` / ``MageRideRadius`` /
/// ``MageRideControl``, and a later screen group takes a shape from here rather than redrawing one.
///
/// **Nothing here keys a `ForEach` on a tuple.** `ForEach(Array(x.enumerated()), id: \.offset)` does
/// not compile — Swift has no key paths into tuples, which is the finding `apps/driver-ios` records
/// from C087 onwards — so both loops below walk indices, exactly as ``StartCodeCard`` does.

// MARK: - The handoff bar

/// SCR-PI-020 and SCR-PI-021's `.stepper` — dots joined by rules, filled up to the current step, with
/// the four `t-small` captions under them.
///
/// The captions are a second row rather than a label inside each dot, because that is how both cells
/// draw it and because giving every caption an equal share of the width is what keeps the four dots
/// evenly spaced when *"Pickup pending"* is three times the length of *"Picked"*.
///
/// `current` is an index into `titleKeys` and is **clamped**: US-20.7's bar is four states, and a
/// fifth value arriving from a later contract must not paint the whole bar or none of it.
struct StepperBar: View {

    let titleKeys: [String]
    let current: Int

    var body: some View {
        VStack(spacing: MageRideSpacing.xxs) {
            HStack(spacing: 0) {
                ForEach(titleKeys.indices, id: \.self) { index in
                    if index > 0 {
                        Rectangle()
                            .fill(colour(reachedBy: index))
                            .frame(height: MageRideControl.stepperLine)
                    }
                    Circle()
                        .fill(colour(reachedBy: index))
                        .frame(width: MageRideControl.stepperDot, height: MageRideControl.stepperDot)
                }
            }

            HStack(alignment: .top, spacing: MageRideSpacing.xxs) {
                ForEach(titleKeys.indices, id: \.self) { index in
                    Text(key: titleKeys[index])
                        .mageFont(index == clamped ? .label : .caption)
                        .foregroundStyle(
                            index <= clamped ? MageRideColor.onSurface : MageRideColor.onSurfaceVariant
                        )
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: .infinity)
                }
            }
        }
        // One announcement rather than eight elements: what a passenger wants read out is *"In
        // transit — step 3 of 4"*, not four dots and four captions in whatever order the reader
        // assembles them.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(currentTitle))
        .accessibilityValue(Text("package_step_of".localisedFormat(clamped + 1, titleKeys.count)))
    }

    /// The current step, kept inside the bar. See the type's note.
    private var clamped: Int { min(max(current, 0), max(titleKeys.count - 1, 0)) }

    private var currentTitle: String {
        titleKeys.indices.contains(clamped) ? titleKeys[clamped].localised : ""
    }

    private func colour(reachedBy index: Int) -> Color {
        index <= clamped ? MageRideColor.primary : MageRideColor.surfaceVariant
    }
}

// MARK: - The handover code

/// The wireframe's `card fill` around *"Pickup OTP"* / *"Delivery OTP — give on handover"* and its
/// four filled boxes.
///
/// **`code` is `nil` more often than not, and that is a recorded contract gap rather than a missing
/// read** — see ``PackageOtps``. The pickup code comes back once on `POST /v1/rides/request` and the
/// delivery code arrives only on the `package_picked_up` push, so a cold start, a reinstall or a
/// handset that never received the push has neither. The card then says so instead of drawing four
/// empty boxes that read as a loading state.
///
/// Deliberately **not** ``StartCodeCard``, which is C098's and draws a *rider* start OTP that no
/// operation on this platform issues at all. The two look alike and mean different things: this one
/// is a code that genuinely exists, which this app may simply not have been told.
struct HandoverCodeCard: View {

    let titleKey: String
    let code: String?

    var body: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Text(key: titleKey)
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)

            if code != nil {
                HStack(spacing: MageRideSpacing.xs) {
                    ForEach(0..<HandoverCodeCard.digits, id: \.self) { index in
                        Text(digit(at: index))
                            .mageFont(.title)
                            .foregroundStyle(MageRideColor.onPrimaryContainer)
                            .frame(minWidth: MageRideControl.otpBox, minHeight: MageRideControl.otpBox)
                            .background(
                                MageRideColor.primaryContainer,
                                in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                            )
                    }
                }
            } else {
                Text(key: "package_otp_unavailable")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        // Read as one sentence, with the digits spaced so VoiceOver says *"four, eight, two, nine"*
        // rather than *"four thousand eight hundred and twenty-nine"* — a handover code is read out
        // to a driver, not counted.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(key: titleKey))
        .accessibilityValue(Text(spokenValue))
    }

    private func digit(at index: Int) -> String {
        let characters = Array(code ?? "")
        return index < characters.count ? String(characters[index]) : " "
    }

    private var spokenValue: String {
        guard let code else { return "package_otp_unavailable".localised }
        return code.map(String.init).joined(separator: " ")
    }

    /// `^\d{4}$` — the pattern every OTP on this platform is fixed to.
    static let digits = 4
}
