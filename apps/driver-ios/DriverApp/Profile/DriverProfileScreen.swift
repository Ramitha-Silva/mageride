import MageRideShared
import SwiftUI
import UIKit

/// **SCR-DI-029 · driver profile** (US-18.3, AL-13).
///
/// The wireframe: a large *"Profile"* title, the identity card, then **Vehicle details**, **Per-trip
/// ratings**, **Emergency contact** and **Driver Level** as grouped rows. The C092 deliverable adds
/// three the sketch has no room for and D2' §SCR-DI-029 implies — the **language**, the **notification
/// switches** (US-10.7) and **log out**.
///
/// **A `List`, which is D2' §SCR-DI-029's own SwiftUI column** (*"`Form`"*): every row here is a
/// settings row with a disclosure, a value or a switch, which is the one shape `Form` exists for. The
/// hand-built ``GroupedList`` that carries the wizard's rows is the wrong control for a screen that is
/// nothing but rows.
///
/// - Parameters:
///   - onOpenVehicles: *"Vehicle details ›"* — SCR-DI-026, which is where a driver's vehicles are.
///   - onOpenRatings: *"Per-trip ratings ›"* — SCR-DI-030, which is where the per-trip stars are
///     (US-18.3). Not a screen of its own: the ratings live on the trips.
///   - onOpenLevel: *"Driver Level  L3 ›"* — SCR-DI-019.
///
/// **Nothing here navigates on log out.** `AuthSessionManager` raises `RouteToLogin` and
/// ``DriverShellModel`` is the single subscriber; a second handler would reset the stacks twice.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct DriverProfileScreen: View {

    @StateObject private var model: DriverProfileModel

    private let onOpenVehicles: () -> Void
    private let onOpenRatings: () -> Void
    private let onOpenLevel: () -> Void

    @State private var isCopied = false

    init(
        model: @autoclosure @escaping () -> DriverProfileModel,
        onOpenVehicles: @escaping () -> Void,
        onOpenRatings: @escaping () -> Void,
        onOpenLevel: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: model())
        self.onOpenVehicles = onOpenVehicles
        self.onOpenRatings = onOpenRatings
        self.onOpenLevel = onOpenLevel
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
                identityCard
            }

            Section {
                // Δ MCS-24 — the platform id, displaced from the header and kept copyable.
                if let userId = model.state.profile?.userId {
                    row(
                        titleKey: "profile_platform_id",
                        symbolName: isCopied ? "checkmark" : "doc.on.doc",
                        tint: MageRideColor.secondary,
                        value: userId
                    ) {
                        UIPasteboard.general.string = userId
                        isCopied = true
                        // The tick is the acknowledgement, not a state: a checkmark that never went
                        // back would say "copied" to a driver returning an hour later.
                        Task {
                            try? await Task.sleep(nanoseconds: Self.copiedTick)
                            isCopied = false
                        }
                    }
                    .textSelection(.enabled)
                }

                row(titleKey: "profile_vehicle_details", symbolName: "car.fill", tint: MageRideColor.secondary) {
                    onOpenVehicles()
                }
                row(titleKey: "profile_trip_ratings", symbolName: "star.fill", tint: MageRideColor.warning) {
                    onOpenRatings()
                }
                row(
                    titleKey: "profile_emergency_row",
                    symbolName: "sos.circle.fill",
                    tint: MageRideColor.error,
                    value: model.state.emergencyText,
                    fallbackValueKey: "profile_emergency_none"
                ) {
                    model.open(.emergency)
                }
                row(
                    titleKey: "profile_driver_level",
                    symbolName: "trophy.fill",
                    tint: MageRideColor.primary,
                    value: model.state.levelText ?? MageRideSymbols.unknown
                ) {
                    onOpenLevel()
                }
                row(
                    titleKey: "profile_language_row",
                    symbolName: "character.bubble.fill",
                    tint: MageRideColor.success,
                    value: model.state.profile?.language.map(LanguageDisplay.endonym)
                ) {
                    model.open(.language)
                }
            }

            notificationSwitches

            Section {
                Button(role: .destructive) {
                    Task { await model.logOut() }
                } label: {
                    Text(key: "profile_log_out")
                        .frame(maxWidth: .infinity)
                }
                .disabled(model.state.isSaving)
            }
        }
        .listStyle(.insetGrouped)
        .navigationTitle(Text(key: "profile_title"))
        .navigationBarTitleDisplayMode(.large)
        .task {
            // Δ MCS-27 — the cache first, so the header opens on a name; the reads refresh behind it.
            await model.paintFromCache()
            await model.refresh()
        }
        .refreshable { await model.refresh() }
        .sheet(item: Binding(get: { model.state.sheet }, set: { if $0 == nil { model.dismissSheet() } })) { sheet in
            ProfileEditorSheet(sheet: sheet, model: model)
        }
    }

    // MARK: - The identity card

    /// The wireframe's avatar card — *"K. Fernando · DRV-22011 · ★4.8 overall"*.
    ///
    /// **Δ MCS-24 — the platform id is gone from here and the live vehicle is in its place.** The id
    /// is real and this is the screen a driver reads it off before another driver types it into
    /// SCR-DI-023 — but it does not belong on the line that answers "who am I and what am I
    /// driving", and the wireframe's own second line is the vehicle and the rating, not an
    /// identifier. It moves to a row of its own, still copyable, because C091's handoff is explicit
    /// that a Driver ID which can only be read aloud is a credit transfer nobody completes.
    ///
    /// The layout is ``DriverHeader``, which SCR-DI-036's Menu tab also draws.
    ///
    /// The star average has **no read on the app-facing surface at all** — see
    /// ``DriverHeaderState`` — so the line carries the vehicle alone until one exists.
    private var identityCard: some View {
        DriverHeader(
            state: DriverHeaderState(
                name: model.state.profile?.firstName,
                level: model.state.standing.standing?.level,
                registration: model.state.registration,
                // No app-facing read carries a driver's own star average — see ``DriverHeaderState``.
                rating: nil,
                photo: model.state.photo
            )
        ) {
            Button {
                model.open(.name)
            } label: {
                Image(systemName: "pencil")
                    .foregroundStyle(MageRideColor.primary)
            }
            .buttonStyle(.plain)
        }
    }

    private func row(
        titleKey: String,
        symbolName: String,
        tint: Color,
        value: String? = nil,
        fallbackValueKey: String? = nil,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            HStack(spacing: MageRideSpacing.sm) {
                Image(systemName: symbolName)
                    .font(.footnote)
                    .foregroundStyle(MageRideColor.onStatus)
                    .frame(width: MageRideControl.listRowIcon, height: MageRideControl.listRowIcon)
                    .background(tint, in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous))

                VStack(alignment: .leading, spacing: 1) {
                    Text(key: titleKey)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)

                    if let value {
                        Text(value)
                            .mageFont(.label)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                    } else if let fallbackValueKey {
                        Text(key: fallbackValueKey)
                            .mageFont(.label)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                    }
                }

                Spacer(minLength: MageRideSpacing.xs)

                Image(systemName: "chevron.right")
                    .font(.footnote)
                    .foregroundStyle(MageRideColor.outlineVariant)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }

    // MARK: - The switches

    /// How long the copy button shows its tick.
    private static let copiedTick: UInt64 = 1_500_000_000

    /// US-10.7's per-type switches, grouped — see ``DriverNotificationGroup`` for why five and not
    /// fifteen, and why nothing safety-critical is offered.
    private var notificationSwitches: some View {
        Section {
            ForEach(DriverNotificationGroup.allCases) { group in
                Toggle(
                    isOn: Binding(
                        get: { group.isEnabled(in: model.state.notificationPreferences) },
                        set: { isEnabled in
                            Task { await model.setNotificationGroup(group, isEnabled: isEnabled) }
                        }
                    )
                ) {
                    Text(key: group.labelKey)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                }
                .disabled(model.state.isSaving || model.state.profile == nil)
            }
        } header: {
            SectionLabel(key: "profile_notify_heading")
        } footer: {
            Text(key: "profile_notify_safety_note")
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
    }
}
