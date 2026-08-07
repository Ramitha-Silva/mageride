import MageRideShared
import SwiftUI

/// **SCR-DI-028 · sharing management (Mode B), per vehicle** (US-4.1–4.4, US-4.7/4.8, AL-35).
///
/// The wireframe, top to bottom: a `‹ Menu` / *"Sharing (Mode B)"* bar, the **Vehicle** label and its
/// full-device-width selector, the **Share with User ID** and **Expiry** rows, the `Grant access` CTA,
/// *"Incoming requests · this vehicle"* and *"Current grantees · this vehicle"*.
///
/// **Δ iOS — the cell's own clause is *"vehicle `Picker` + `.swipeActions` accept/reject"*, and both
/// are drawn here.** The chip row becomes a segmented `Picker`, which is what a full-device-width
/// one-of-N selector is on this platform (`.seg` is `UISegmentedControl` in the wireframe's own CSS —
/// the same reading C090 and C091 made of SCR-DI-020's periods and SCR-DI-022's methods). A segment
/// holds text and nothing else, so the type dot and the `FLEET` badge the chip carried are drawn on
/// the **selected** vehicle's identity row directly under it. That row is not AL-35's removed caption
/// box: it says which vehicle, in the chip's own words, and never who assigned it.
///
/// **AL-35's fence is drawn here and enforced in the model.** The lists below carry the wireframe's own
/// *"· this vehicle"* in their headings, and ``SharingModel/select(vehicleId:)`` empties them before
/// re-reading so a queue is never seen under the wrong vehicle.
///
/// **A `List`, because `.swipeActions` exists only inside one** — the same reason SCR-DI-026 is one.
/// Android draws Accept and Reject as two text buttons in the row; here they are the swipe the
/// wireframe's `Δ iOS` clause asks for, and the footnote announces it.
@MainActor
struct SharingScreen: View {

    @StateObject private var model: SharingModel

    @State private var isPickingExpiry = false

    init(model: @autoclosure @escaping () -> SharingModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        List {
            if let errorKey = model.state.errorKey {
                Section {
                    FormErrorText(messageKey: errorKey)
                        .listRowBackground(Color.clear)
                        .onTapGesture(perform: model.dismissError)
                }
            }

            Section {
                vehiclePicker
                if let selected = model.state.selected {
                    identityRow(selected)
                }
            } header: {
                SectionLabel(key: "sharing_vehicle_label")
            }

            if model.state.hasNoShareableVehicle {
                Section {
                    Text(key: "sharing_no_vehicles")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
            } else {
                shareForm
                requestQueue
                granteeList
            }
        }
        .listStyle(.insetGrouped)
        .navigationTitle(Text(key: "sharing_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.refresh() }
        .refreshable { await model.refresh() }
        .sheet(isPresented: $isPickingExpiry) {
            ExpirySheet(current: model.state.expiresAt) { expiresAt in
                isPickingExpiry = false
                model.onExpiryChange(expiresAt)
            }
        }
    }

    // MARK: - The selector

    /// The wireframe's full-device-width chip row, as this platform's one-of-N control.
    ///
    /// The registration number alone, because that is what fits in a segment and what identifies a
    /// vehicle to its driver; the type and the fleet marker are on the row underneath.
    private var vehiclePicker: some View {
        Picker(
            selection: Binding(
                get: { model.state.selectedVehicleId ?? "" },
                set: { vehicleId in Task { await model.select(vehicleId: vehicleId) } }
            )
        ) {
            ForEach(model.state.vehicles, id: \.vehicleId) { vehicle in
                Text(vehicle.registrationNumber).tag(vehicle.vehicleId)
            }
        } label: {
            Text(key: "sharing_vehicle_label")
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .disabled(model.state.vehicles.count < 2)
        .listRowBackground(MageRideColor.background)
    }

    /// *"● Van · VN-3321  FLEET"* — the selected chip's own content, which a segment cannot hold.
    ///
    /// The dot is MAP-03's per-type colour and the badge is US-13.9's temporarily-assigned marker;
    /// `VehicleSummary.fleetName` is what registry sends for one.
    private func identityRow(_ vehicle: VehicleSummary) -> some View {
        HStack(spacing: MageRideSpacing.xs) {
            VehicleTypeDot(token: VehicleToken.forVehicleType(vehicle.vehicleType))

            Text(vehicle.vehicleType.labelKey.localised + MageRideSymbols.separator + vehicle.registrationNumber)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurface)

            Spacer(minLength: MageRideSpacing.xxs)

            if vehicle.fleetName != nil {
                SolidBadge(label: "sharing_fleet_badge".localised, accent: MageRideVehicleColor.modeB)
            }
        }
        .accessibilityElement(children: .combine)
    }

    // MARK: - The share form

    /// The wireframe's `Share with User ID` and `Expiry  30 Jun ›` rows over the `Grant access` CTA.
    @ViewBuilder
    private var shareForm: some View {
        Section {
            DriverIdField(
                labelKey: "sharing_user_id_label",
                value: Binding(get: { model.state.userId }, set: model.onUserIdChange),
                supportingKey: model.state.isUserIdRejected ? "sharing_user_id_invalid" : "sharing_user_id_help",
                isError: model.state.isUserIdRejected
            )
            .listRowInsets(EdgeInsets())
            .listRowBackground(Color.clear)

            Button { isPickingExpiry = true } label: {
                HStack(spacing: MageRideSpacing.xs) {
                    Text(key: "sharing_expiry_label")
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                    Spacer(minLength: MageRideSpacing.xxs)
                    Text(model.state.expiresAt.map(ShareExpiry.label) ?? "sharing_expiry_none".localised)
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                    Image(systemName: "chevron.right")
                        .font(.footnote)
                        .foregroundStyle(MageRideColor.outlineVariant)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            Button {
                Task { await model.grant() }
            } label: {
                Text(key: "sharing_grant_action")
            }
            .buttonStyle(.mageCta(loading: model.state.isGranting))
            .disabled(!model.state.canGrant)
            .listRowInsets(EdgeInsets())
            .listRowBackground(Color.clear)

            // US-4.3b — the grant is pending until the passenger accepts, so this is an offer sent and
            // not a subscriber gained. The grantee list below is deliberately not touched until they
            // answer.
            if model.state.grantedTo != nil {
                Text(key: "sharing_granted")
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.secondary)
                    .listRowBackground(Color.clear)
            }
        }
    }

    // MARK: - The two lists

    /// *"Incoming requests · this vehicle"* — US-4.4, scoped by the selector and never mixed.
    @ViewBuilder
    private var requestQueue: some View {
        Section {
            if model.state.isReadingLists {
                ProgressView().frame(maxWidth: .infinity)
            } else if model.state.requests.isEmpty {
                Text(key: "sharing_requests_empty")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            } else {
                ForEach(model.state.requests, id: \.requestId) { request in
                    partyRow(
                        name: request.passengerName,
                        contact: [request.passengerMobileMasked, request.passengerId]
                    )
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) {
                            Task { await model.decide(requestId: request.requestId, isAccepted: false) }
                        } label: {
                            Label {
                                Text(key: "sharing_reject")
                            } icon: {
                                Image(systemName: "xmark")
                            }
                        }
                    }
                    // `allowsFullSwipe: false` on the admitting edge, deliberately: a full swipe is one
                    // gesture with no confirmation, and this one starts a subscription on somebody
                    // else's account. Rejecting is recoverable — the passenger can ask again.
                    .swipeActions(edge: .leading, allowsFullSwipe: false) {
                        Button {
                            Task { await model.decide(requestId: request.requestId, isAccepted: true) }
                        } label: {
                            Label {
                                Text(key: "sharing_accept")
                            } icon: {
                                Image(systemName: "checkmark")
                            }
                        }
                        .tint(MageRideColor.success)
                    }
                    .disabled(model.state.busyRequestId != nil)
                }

                Text(key: "sharing_swipe_hint")
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .listRowBackground(Color.clear)
            }
        } header: {
            SectionLabel(key: "sharing_requests_heading")
        }
    }

    /// *"Current grantees · this vehicle"* — who can track it right now (US-4.7).
    @ViewBuilder
    private var granteeList: some View {
        Section {
            if model.state.grantees.isEmpty {
                Text(key: "sharing_grantees_empty")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            } else {
                ForEach(model.state.grantees, id: \.userId) { grantee in
                    HStack(spacing: MageRideSpacing.xs) {
                        partyRow(name: grantee.name, contact: [grantee.phoneMasked, grantee.userId])
                        StatusPill(label: "sharing_status_active".localised, tone: .done)
                    }
                }
            }
        } header: {
            SectionLabel(key: "sharing_grantees_heading")
        }
    }

    /// *"Sunethra · +94 77 712 0345 · PAX-77120"*, as the two lists print a passenger.
    ///
    /// The number is `passengerMobileMasked` / `phoneMasked` and arrives **masked** (AL-40/41/42); the
    /// wireframe prints a full one, and a client cannot unmask what the directory never sent. The id
    /// under it is the passenger's platform id — see ``PlatformId`` on why it is not `PAX-77120`.
    private func partyRow(name: String?, contact: [String?]) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(name ?? "sharing_passenger_unnamed".localised)
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)
            Text(contact.compactMap { $0 }.joined(separator: MageRideSymbols.separator))
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

/// The wireframe's `Expiry  30 Jun ›` picker.
///
/// Open-ended until a date is chosen, because `CreateShareGrantRequest.expiresAt` is optional and
/// *"open-ended when omitted"*. The calendar and the zone are ``ScheduleLabels``', so the day the driver
/// taps **is** a Colombo day — see ``ShareExpiry`` for the hop that removes, and for the one that is
/// left. `in: Date()...` because a grant that lapsed before it was made is not a grant.
private struct ExpirySheet: View {

    let onConfirm: (Timestamp?) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var picked: Date

    init(current: Timestamp?, onConfirm: @escaping (Timestamp?) -> Void) {
        self.onConfirm = onConfirm
        _picked = State(initialValue: current.map(ShareExpiry.date) ?? Date())
    }

    var body: some View {
        NavigationStack {
            Form {
                DatePicker(selection: $picked, in: Date()..., displayedComponents: .date) {
                    Text(key: "sharing_expiry_label")
                }

                Button { onConfirm(nil) } label: {
                    Text(key: "sharing_expiry_clear")
                }
            }
            .environment(\.calendar, ScheduleLabels.calendar)
            .environment(\.timeZone, ScheduleLabels.zone)
            .navigationTitle(Text(key: "sharing_expiry_title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button { dismiss() } label: { Text(key: "action_cancel") }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button { onConfirm(ShareExpiry.endOfDay(picked)) } label: { Text(key: "action_save") }
                }
            }
        }
        .presentationDetents([.medium])
    }
}
