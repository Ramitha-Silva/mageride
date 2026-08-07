import MageRideShared
import SwiftUI

/// **SCR-DI-030 · ride history + trip details + rate passenger** (US-8.8, US-18.2, AL-35).
///
/// The wireframe: a large *"Ride history"* title over a list of trip cards — route and fare on the
/// first line, *"17 Jun · 8 km · fare to you Rs 480"* and either a **Rate ★** link or the stars already
/// given on the second — with the rate-passenger sheet over it.
///
/// **AL-35's fence: rating opens a sheet, not an inline card.** ``RatePassengerSheet`` is that sheet, at
/// the `.medium` detent D2' §SCR-DI-030's own `Δ iOS` clause names; nothing on this screen expands in
/// place.
///
/// **"fare to you" is the fare.** D5' has no commission and AL-01 removed the last thing that could have
/// taken one — what the passenger paid is what the driver keeps, less the flat daily fee that
/// SCR-DI-021 accounts for separately. So the row's two amounts are the same number, which is what the
/// wireframe draws, and it is not a mistake in the sketch.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct RideHistoryScreen: View {

    @StateObject private var model: RideHistoryModel

    init(model: @autoclosure @escaping () -> RideHistoryModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        Group {
            if model.state.isEmpty {
                empty
            } else {
                trips
            }
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "history_title"))
        .navigationBarTitleDisplayMode(.large)
        .task { await model.refresh() }
        // `isPresented` rather than `item:`, deliberately. The sheet's contents change **while it is
        // up** — the passenger's name lands from a second read a moment after it opens — and
        // `sheet(item:)` hands the builder the value it was presented with. Binding the presentation
        // to *whether* there is a rating and reading the rating itself off the observed model is what
        // makes the CTA come alive when `GET /v1/rides/{rideId}` answers.
        .sheet(
            isPresented: Binding(
                get: { model.state.rating != nil },
                set: { if !$0 { model.dismissRating() } }
            )
        ) {
            RatePassengerSheet(model: model)
        }
    }

    // MARK: - The list

    private var trips: some View {
        List {
            if model.state.isLoading {
                ProgressView()
                    .frame(maxWidth: .infinity)
                    .listRowBackground(Color.clear)
            }

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
                    .listRowBackground(Color.clear)
                    .onTapGesture(perform: model.dismissError)
            }

            ForEach(model.state.trips) { trip in
                TripCard(trip: trip) {
                    Task { await model.openRating(tripId: trip.id) }
                }
                .listRowInsets(EdgeInsets(
                    top: MageRideSpacing.xxs,
                    leading: MageRideSpacing.md,
                    bottom: MageRideSpacing.xxs,
                    trailing: MageRideSpacing.md
                ))
                .listRowBackground(MageRideColor.background)
                .listRowSeparator(.hidden)
            }
        }
        .listStyle(.plain)
        .refreshable { await model.refresh() }
    }

    private var empty: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Spacer(minLength: 0)
            Image(systemName: "list.bullet.rectangle.portrait")
                .font(.system(size: MageRideControl.illustrationIcon))
                .foregroundStyle(MageRideColor.outlineVariant)
            Text(key: "history_empty")
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(MageRideSpacing.lg)
    }
}

/// One trip, as the wireframe's `.card` draws it.
private struct TripCard: View {

    let trip: HistoryTrip
    let onRate: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
                Text(trip.routeText)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                    .frame(maxWidth: .infinity, alignment: .leading)

                Text(trip.summary.fareMinor.map { MoneyFormat.rupees($0.int64Value) } ?? MoneyFormat.empty)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
            }

            HStack(spacing: MageRideSpacing.xs) {
                Text(trip.captionText)
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .frame(maxWidth: .infinity, alignment: .leading)

                if let rating = trip.rating {
                    Text(RatingStars.text(rating))
                        .mageFont(.bodyEmphasis)
                        .foregroundStyle(MageRideColor.warning)
                        .accessibilityLabel(Text("history_rated_value".localisedFormat(rating)))
                } else if trip.isRateable {
                    Button(action: onRate) {
                        Text("history_rate_action".localised + " " + MageRideSymbols.starFilled)
                            .mageFont(.bodySmall)
                            .foregroundStyle(MageRideColor.primary)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }
}

/// `★★★★★` / `★★★☆☆` — a rating drawn the way the wireframe draws it.
enum RatingStars {

    static func text(_ given: Int) -> String {
        let filled = min(max(given, RatePassengerState.minStars), RatePassengerState.maxStars)
        return String(repeating: MageRideSymbols.starFilled, count: filled)
            + String(repeating: MageRideSymbols.starEmpty, count: RatePassengerState.maxStars - filled)
    }
}
