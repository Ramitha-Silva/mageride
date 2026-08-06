import MageRideShared
import SwiftUI

/// **SCR-DI-026 · vehicle management** and **SCR-DI-026a · the no-vehicles alert**.
///
/// The wireframe top to bottom: a `‹ Menu` / *"My vehicles"* / `＋` bar, the blue banner *"Only one
/// vehicle can be live at a time"*, the **My vehicles** group with a coloured type dot, the
/// registration and either a `✓` or a `Resume ›` link per row, then the separate **Temporarily
/// assigned to me** `FLEET` group, and the footnote *"Swipe a row to deactivate (US-2.16)."*
///
/// **A `List`, unlike every other screen in this cluster.** The cell's own `Δ iOS` clause is
/// *"`.swipeActions` deactivate"*, and that modifier exists only inside a `List` — so the hand-built
/// ``GroupedList`` that carries the wizard's rows is the wrong shape here, and `.insetGrouped` is
/// what draws the wireframe's `.glist` natively. Android puts Remove and Documents in a row of text
/// buttons; on this platform they are the swipe the wireframe asks for and the footnote announces.
///
/// - Parameters:
///   - onAddVehicle: The ＋, a row's Resume, and 026a's *"Yes, onboard ›"*. All three open the
///     wizard, and ``VehicleOnboardingRepository/resume()`` decides whether that is a resume or a
///     **new** vehicle at Step 1/4 (US-2.27).
///   - onOpenStatus: Opens SCR-DI-006 for a vehicle whose documents are being verified.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct VehiclesScreen: View {

    @StateObject private var model: VehiclesModel

    private let onAddVehicle: () -> Void
    private let onOpenStatus: () -> Void

    init(
        vehicles: VehicleOnboardingRepository,
        session: VehicleOnboardingSession,
        activeVehicle: ActiveVehicleStore,
        onAddVehicle: @escaping () -> Void,
        onOpenStatus: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: VehiclesModel(vehicles: vehicles, session: session, activeVehicle: activeVehicle)
        )
        self.onAddVehicle = onAddVehicle
        self.onOpenStatus = onOpenStatus
    }

    var body: some View {
        List {
            Section {
                NoticeCard(symbolName: "info.circle.fill", accent: MageRideColor.primary) {
                    Text(key: "vehicles_one_live")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurface)
                }
                .listRowInsets(EdgeInsets())
                .listRowBackground(Color.clear)
            }

            if let errorKey = model.state.errorKey {
                Section {
                    FormErrorText(messageKey: errorKey)
                        .listRowBackground(Color.clear)
                }
            }

            if model.state.isEmpty {
                Section { emptyVehicles.listRowBackground(Color.clear) }
            }

            if !model.state.owned.isEmpty {
                Section {
                    ForEach(model.state.owned) { row in
                        vehicleRow(row).swipeActions(edge: .trailing) { ownedActions(row) }
                    }
                } header: {
                    SectionLabel(key: "vehicles_mine_label")
                }
            }

            if !model.state.assigned.isEmpty {
                Section {
                    // No swipe actions: a fleet vehicle is not this driver's to remove, and its
                    // onboarding was the Fleet Portal's (AL-27, US-13.9).
                    ForEach(model.state.assigned) { row in
                        vehicleRow(row)
                    }
                } header: {
                    HStack(spacing: MageRideSpacing.xs) {
                        SectionLabel(key: "vehicles_assigned_label")
                        StatusPill(label: "vehicles_fleet_badge".localised, tone: .neutral)
                    }
                }
            }

            if !model.state.owned.isEmpty {
                Section {
                    Text(key: "vehicles_swipe_hint")
                        .mageFont(.label)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .listRowBackground(Color.clear)
                }
            }
        }
        .listStyle(.insetGrouped)
        .navigationTitle(Text(key: "vehicles_title"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                Button(action: onAddVehicle) {
                    Image(systemName: "plus")
                }
                .accessibilityLabel(Text(key: "vehicles_add"))
            }
        }
        .refreshable { await model.refresh() }
        .task { await model.refresh() }
        // **SCR-DI-026a** — *"Onboard a standby vehicle?"*, an `.alert` exactly as the cell's own
        // `Δ iOS` clause says.
        .alert(
            Text(key: "vehicles_onboard_prompt_title"),
            isPresented: Binding(
                get: { model.state.isOnboardPromptVisible },
                set: { if !$0 { model.dismissOnboardPrompt() } }
            )
        ) {
            Button(role: .cancel, action: model.dismissOnboardPrompt) {
                Text(key: "vehicles_onboard_prompt_not_now")
            }
            Button {
                model.dismissOnboardPrompt()
                onAddVehicle()
            } label: {
                Text(key: "vehicles_onboard_prompt_yes")
            }
        } message: {
            Text(key: "vehicles_onboard_prompt_body")
        }
        // US-2.16's confirm. Deactivating releases the plate (D-37), so it is not a silent action.
        .alert(
            Text(key: "vehicles_deactivate_title"),
            isPresented: Binding(
                get: { model.state.deactivating != nil },
                set: { if !$0 { model.cancelDeactivate() } }
            ),
            presenting: model.state.deactivating
        ) { _ in
            Button(role: .cancel, action: model.cancelDeactivate) {
                Text(key: "action_dismiss")
            }
            Button(role: .destructive) {
                Task { await model.deactivate() }
            } label: {
                Text(key: "vehicles_deactivate")
            }
        } message: { row in
            Text("vehicles_deactivate_body".localisedFormat(row.summary.registrationNumber))
        }
    }

    // MARK: - A row

    /// One vehicle.
    ///
    /// The trailing control is the wireframe's `✓` / `Select ›` / `Resume ›`, and the selection is
    /// **only live on a vehicle that can go live** (US-9.6): an Incomplete Mode C vehicle offers
    /// Resume in its place, because selecting a vehicle that cannot be dispatched is not a state the
    /// driver should be able to reach.
    private func vehicleRow(_ row: VehicleRow) -> some View {
        let isActive = row.vehicleId == model.state.activeVehicleId
        let status = row.status(isActive: isActive)

        return Button {
            if row.canGoLive {
                model.select(row)
            } else if row.isIncomplete {
                resume(row)
            }
        } label: {
            HStack(spacing: MageRideSpacing.xs) {
                VehicleTypeDot(token: row.token)

                VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
                    Text(row.titleText)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)

                    if let caption = row.assignmentCaption {
                        Text(caption)
                            .mageFont(.label)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                    }

                    StatusPill(label: status.label, tone: status.tone)
                }

                Spacer(minLength: MageRideSpacing.xs)
                trailing(for: row, isActive: isActive)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isActive ? [.isButton, .isSelected] : .isButton)
    }

    @ViewBuilder
    private func trailing(for row: VehicleRow, isActive: Bool) -> some View {
        if isActive {
            Image(systemName: "checkmark")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(MageRideColor.primary)
        } else if row.canGoLive {
            chevronLabel(key: "vehicles_select")
        } else if row.isIncomplete {
            chevronLabel(key: "vehicles_resume")
        }
    }

    private func chevronLabel(key: String) -> some View {
        HStack(spacing: 2) {
            Text(key: key)
                .mageFont(.bodySmall)
            Image(systemName: "chevron.right")
                .font(.footnote)
        }
        .foregroundStyle(MageRideColor.primary)
    }

    /// The wireframe's `.swipeActions` — Documents and Remove, on an owned vehicle only.
    @ViewBuilder
    private func ownedActions(_ row: VehicleRow) -> some View {
        Button(role: .destructive) {
            model.confirmDeactivate(row)
        } label: {
            Label {
                Text(key: "vehicles_deactivate")
            } icon: {
                Image(systemName: "trash")
            }
        }

        Button {
            model.open(row)
            onOpenStatus()
        } label: {
            Label {
                Text(key: "vehicles_view_status")
            } icon: {
                Image(systemName: "doc.text")
            }
        }
        .tint(MageRideColor.primary)
    }

    private func resume(_ row: VehicleRow) {
        model.open(row)
        onAddVehicle()
    }

    /// The wireframe's 🚗 *"No vehicles yet"* block behind the 026a alert.
    private var emptyVehicles: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Image(systemName: "car.fill")
                .font(.system(size: MageRideControl.illustrationIcon))
                .foregroundStyle(MageRideColor.outlineVariant)
            Text(key: "vehicles_empty_title")
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
            Text(key: "vehicles_empty_body")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, MageRideSpacing.xl)
    }
}
