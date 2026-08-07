import MageRideShared
import SwiftUI

/// SCR-PI-013 — a ride in the future (US-10.1a, US-6A.4).
///
/// The cell: `‹ Back · Schedule ride · Done`, a **Where to?** pair of dotted fields with the
/// destination highlighted, a graphical calendar, a time card, the reminders note, and *"Confirm
/// schedule"*.
///
/// **The destination is mandatory and Confirm says so** (AL-36). A time alone is not a booking, and
/// the cell's own state line is explicit: *"Confirm disabled until a destination is set"*. The gate
/// is ``ScheduleRideState/canConfirm``, so it holds whatever the layout does.
///
/// **Δ iOS:** one `DatePicker(.graphical)` where the cell draws a calendar card and a time card
/// separately. That is the cell's own clause — `DatePicker(.graphical)` is the platform control it
/// names, and its `.dateAndTime` mode *is* those two cards, drawn by the system with the
/// past-date bound the cell asks for.
@MainActor
struct ScheduleRideScreen: View {

    @StateObject private var model: ScheduleRideModel

    let onBack: () -> Void
    let onPickDestination: () -> Void
    let onScheduled: () -> Void

    init(
        draft: BookingDraft,
        bookings: BookingRepository,
        onBack: @escaping () -> Void,
        onPickDestination: @escaping () -> Void,
        onScheduled: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: ScheduleRideModel(draft: draft, bookings: bookings))
        self.onBack = onBack
        self.onPickDestination = onPickDestination
        self.onScheduled = onScheduled
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                SectionLabel(key: "schedule_where_to")

                RouteFieldRow(
                    dotColor: MageRideColor.success,
                    value: model.state.pickup?.address ?? "search_current_location".localised,
                    isPlaceholder: model.state.pickup?.address == nil
                )

                RouteFieldRow(
                    dotColor: MageRideColor.error,
                    value: model.state.dropoff?.address ?? "schedule_select_destination".localised,
                    isPlaceholder: model.state.dropoff == nil,
                    // The wireframe outlines this field in `primary` because it is the one thing the
                    // screen is waiting for. Once it is filled in, it stops shouting.
                    isHighlighted: model.state.dropoff == nil,
                    symbolName: "magnifyingglass",
                    action: onPickDestination
                )

                // `in:` rather than a validator alone — the system refuses a past day outright, and
                // `setPickupTime` refuses one that went past between opening and confirming.
                DatePicker(
                    "",
                    selection: timeBinding,
                    in: earliest...,
                    displayedComponents: [.date, .hourAndMinute]
                )
                .datePickerStyle(.graphical)
                .labelsHidden()
                .tint(MageRideColor.primary)
                .accessibilityLabel(Text(key: "schedule_when"))

                InfoBanner(messageKey: "schedule_reminders", symbolName: "bell")

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                Button { model.confirm() } label: {
                    Text(key: "schedule_confirm")
                }
                .buttonStyle(.mageCta(loading: model.state.isSaving))
                .disabled(!model.state.canConfirm)
                .padding(.top, MageRideSpacing.xs)
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "schedule_title"))
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { model.refreshPlaces() }
        .onChange(of: model.state.scheduled) { scheduled in
            guard scheduled != nil else { return }
            onScheduled()
            model.onScheduleConsumed()
        }
    }

    /// The `DatePicker`'s binding. Writing through the model rather than to a `@State` is what makes
    /// the T-30 refusal a single rule rather than one here and one in ``ScheduleRideModel``.
    private var timeBinding: Binding<Date> {
        Binding(
            get: { model.state.pickupTime ?? earliest },
            set: { model.setPickupTime($0) }
        )
    }

    /// The earliest instant the Job Board can still carry (US-6A.4/6A.5) — see
    /// ``ScheduleRideModel/minimumLeadSeconds``.
    private var earliest: Date {
        Date().addingTimeInterval(ScheduleRideModel.minimumLeadSeconds)
    }
}
