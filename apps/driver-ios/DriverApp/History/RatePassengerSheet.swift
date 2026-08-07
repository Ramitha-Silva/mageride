import MageRideShared
import SwiftUI

/// **The rate-passenger sheet** — AL-35's ruling, drawn.
///
/// > *"Tapping 'Rate ★' on a completed trip opens a sheet (iOS `.sheet`, detent `.medium`) to rate the
/// > passenger 1–5 stars (+ optional comment) (US-18.2) — **no longer an inline card**."*
///
/// The wireframe's contents, top to bottom: the grab handle (which a presented `.sheet` draws itself),
/// *"Rate passenger"*, the passenger's avatar with their name and the trip under it, the five stars,
/// the optional comment field and the `Submit rating` CTA.
///
/// **One completed ride can never be rated: a proxy booking whose rider is unregistered** (P-01). There
/// is no `iam.users` row to be the `ratee_id`, and `DriverRatingInput.passengerId` is required — so the
/// sheet says so and the CTA stays dead rather than posting a rating about nobody.
///
/// **The sheet reads its own state off the model rather than being handed a value**, because the
/// passenger's name arrives from `GET /v1/rides/{rideId}` a moment *after* the sheet opens — see
/// ``RideHistoryScreen`` for why that rules out `sheet(item:)`.
@MainActor
struct RatePassengerSheet: View {

    @ObservedObject var model: RideHistoryModel

    var body: some View {
        if let rating = model.state.rating {
            sheet(rating)
        }
    }

    private func sheet(_ rating: RatePassengerState) -> some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                passenger(rating)

                StarRow(
                    stars: rating.stars,
                    onStarsChange: model.onStarsChange
                )

                LabelledTextField(
                    labelKey: "history_rate_comment_label",
                    value: Binding(get: { rating.comment }, set: model.onCommentChange),
                    placeholder: "history_rate_comment_hint".localised,
                    autocapitalisation: .sentences
                )

                if !rating.isLoading && rating.passengerId == nil {
                    Text(key: "history_rate_no_passenger")
                        .mageFont(.label)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                Button {
                    Task { await model.submitRating() }
                } label: {
                    Text(key: "history_rate_submit")
                }
                .buttonStyle(.mageCta(loading: rating.isSubmitting))
                .disabled(!rating.canSubmit)

                Spacer(minLength: 0)
            }
            .padding(MageRideSpacing.md)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(MageRideColor.background)
            .navigationTitle(Text(key: "history_rate_title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(action: model.dismissRating) { Text(key: "action_cancel") }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }

    /// The wireframe's avatar and the two lines beside it.
    ///
    /// The photo is the placeholder glyph for ``DriverProfileScreen``'s reason: nothing in this target
    /// loads a remote image.
    private func passenger(_ rating: RatePassengerState) -> some View {
        HStack(spacing: MageRideSpacing.xs) {
            Image(systemName: "person.crop.circle.fill")
                .font(.system(size: MageRideControl.avatarSmall))
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            VStack(alignment: .leading, spacing: 1) {
                Text(rating.passengerName ?? "history_passenger_unnamed".localised)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                Text(
                    rating.trip.routeText
                        + MageRideSymbols.separator
                        + ScheduleLabels.date(rating.trip.summary.startedAt)
                )
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            Spacer(minLength: 0)

            if rating.isLoading {
                ProgressView()
            }
        }
        .accessibilityElement(children: .combine)
    }
}

/// The five stars, as five tap targets.
///
/// `.isSelected` on the chosen one and an `accessibilityValue` on the row: one of five is exactly what a
/// star rating is, and VoiceOver announcing five buttons would leave a driver with no way to know which
/// was chosen. Each target is the HIG minimum, because a mis-tap here writes the wrong rating on
/// somebody's account.
private struct StarRow: View {

    let stars: Int
    let onStarsChange: (Int) -> Void

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Spacer(minLength: 0)
            ForEach(RatePassengerState.minStars...RatePassengerState.maxStars, id: \.self) { star in
                Button { onStarsChange(star) } label: {
                    Text(star <= stars ? MageRideSymbols.starFilled : MageRideSymbols.starEmpty)
                        .mageFont(.headline)
                        .foregroundStyle(MageRideColor.warning)
                        .frame(
                            minWidth: MageRideControl.minimumTapTarget,
                            minHeight: MageRideControl.minimumTapTarget
                        )
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("history_rate_stars".localisedFormat(star)))
                .accessibilityAddTraits(star == stars ? [.isButton, .isSelected] : .isButton)
            }
            Spacer(minLength: 0)
        }
        .accessibilityValue(Text("history_rate_stars".localisedFormat(stars)))
    }
}
