import MageRideShared
import SwiftUI

/// SCR-PI-019 — *"Rate K. Fernando"*.
///
/// The cell: a scrimmed map with a sheet over it, a large avatar, the driver's name, `★★★★☆`, the
/// compliment chips, a comment field and **Submit** (US-18.1). *"star tap scale + haptic"* is its own
/// state line and both live in ``StarRating``.
///
/// **Submit saves rather than sends, and the copy says so**: `ride.yaml` declares no rating operation
/// at all, and trip-state-svc's is scoped to a *session*, which a Mode C ride is not — calling it
/// with a ride id would cross R-01. See ``RideRatings``. Telling a passenger their rating was
/// submitted would be telling them something that did not happen.
@MainActor
struct RateDriverScreen: View {

    @StateObject private var model: RateDriverModel

    /// Saved. Back to wherever the passenger came from — the receipt, or the map.
    let onDone: () -> Void

    init(
        rideId: String,
        rides: RideRepository,
        ratings: RideRatings,
        onDone: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: RateDriverModel(rideId: rideId, rides: rides, ratings: ratings))
        self.onDone = onDone
    }

    var body: some View {
        ScrimmedSheet {
            DriverIdentityRow(name: model.state.driverName, size: MageRideControl.avatarLarge)
                .frame(maxWidth: .infinity)

            StarRating(value: model.state.stars) { model.setStars($0) }
                .frame(maxWidth: .infinity)

            tags

            LabelledTextField(
                labelKey: "rate_comment",
                value: commentBinding,
                placeholder: "rate_comment_hint".localised,
                autocapitalisation: .sentences
            )

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }

            Button { model.submit() } label: { Text(key: "rate_submit") }
                .buttonStyle(.mageCta(loading: model.state.isSubmitting))
                .disabled(!model.state.canSubmit)
        }
        .toolbar(.hidden, for: .navigationBar)
        .task { model.start() }
        .onChange(of: model.state.isQueued) { queued in
            if queued { onDone() }
        }
    }

    /// The cell's wrapping chip row.
    ///
    /// A `FlowRow`-style `Layout` would be the exact CSS, and this app has none; two rows of two is
    /// the same shape at every Dynamic Type size and needs no custom layout — which matters here,
    /// because the four labels are longest in Sinhala and Tamil.
    private var tags: some View {
        VStack(spacing: MageRideSpacing.xs) {
            ForEach(RateDriverScreen.tagRows, id: \.first) { row in
                HStack(spacing: MageRideSpacing.xs) {
                    ForEach(row, id: \.self) { tag in
                        PlaceChip(
                            label: tag.labelKey.localised,
                            symbolName: model.state.tags.contains(tag) ? "checkmark" : "hand.thumbsup"
                        ) {
                            model.toggle(tag)
                        }
                        .opacity(model.state.tags.contains(tag) ? 1 : 0.6)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity)
    }

    private var commentBinding: Binding<String> {
        Binding(
            get: { model.state.comment },
            set: { model.onCommentChanged($0) }
        )
    }

    /// The four tags, two to a row.
    ///
    /// Keyed by `first` in the `ForEach` above rather than by index: Swift has no key paths into
    /// tuples and indexing a collection by position reorders it the moment the data changes — the
    /// finding `apps/driver-ios` records from C087 onwards.
    private static let tagRows: [[RatingTag]] = [
        [.clean, .onTime],
        [.polite, .safeDriving],
    ]
}
