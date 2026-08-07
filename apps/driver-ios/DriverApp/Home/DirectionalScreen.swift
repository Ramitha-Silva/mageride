import MageRideShared
import SwiftUI

/// **SCR-DI-013 · Directional Travel** (US-6A.17–23, DT-01..DT-08).
///
/// The wireframe, top to bottom: the destination field (search, or ★ Home), the map preview with the ➤
/// heading marker, the *"Uses left"* / *"Max"* pair, **Set Direction (uses 1)**, and — when a filter is
/// running — the persistent card with the destination, what is left of it, **Turn Off** and the note
/// that only direction-matching hires reach the driver.
///
/// **Turning it off still costs the use** (US-6A.19). The card says so, because a driver who thinks
/// they are getting the activation back will spend both before lunch.
///
/// A pushed destination with the system's own `‹` back button, which is the wireframe's `‹ Back`:
/// SCR-DI-013 is a screen a driver leaves, unlike SCR-DI-014, which is a takeover they answer.
@MainActor
struct DirectionalScreen: View {

    @StateObject private var model: DirectionalModel

    init(model: @autoclosure @escaping () -> DirectionalModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            VStack(spacing: MageRideSpacing.sm) {
                if let errorKey = model.state.errorKey {
                    DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                        .onTapGesture(perform: model.dismissError)
                }

                destinationField

                // The wireframe's small map with the ➤ heading marker. The arrow is rotated to the
                // bearing from the driver to the destination, not to the vehicle's own course — the
                // point of the preview is "this is the way you said you were going".
                DriverHomeMap(
                    position: model.state.position,
                    token: nil,
                    heading: model.state.headingDeg
                )
                .frame(height: MageRideControl.mapPreview)
                .clipShape(RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))

                HStack(spacing: MageRideSpacing.xs) {
                    MetricCard(labelKey: "directional_uses_left", value: String(model.state.usesRemaining))
                    MetricCard(labelKey: "directional_max_duration", value: maxDurationText)
                }

                Button { Task { await model.setDirection() } } label: {
                    Text(key: "directional_set")
                }
                .buttonStyle(.mageCta(loading: model.state.isBusy))
                .disabled(!model.state.canSet || model.state.isBusy)

                if model.state.isActive {
                    SectionLabel(key: "directional_when_active")
                    activeFilterCard
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "directional_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            model.start()
            await model.refresh()
        }
        .onDisappear(perform: model.stop)
    }

    /// The destination field and its shortlist — a geocoder search over ★ Home and ★ Work.
    private var destinationField: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            LabelledTextField(
                labelKey: "directional_destination",
                value: Binding(get: { model.state.query }, set: model.typed),
                placeholder: "directional_search_hint".localised
            )

            ForEach(model.state.suggestions) { suggestion in
                Button { model.choose(suggestion) } label: {
                    HStack(spacing: MageRideSpacing.xs) {
                        Image(systemName: suggestion.isHome ? "house.fill" : "mappin.circle.fill")
                            .foregroundStyle(MageRideColor.primary)
                        Text(suggestion.label)
                            .mageFont(.bodySmall)
                            .foregroundStyle(MageRideColor.onSurface)
                        Spacer(minLength: 0)
                    }
                    .padding(.vertical, MageRideSpacing.xs)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }

    /// The persistent active banner (US-6A.21, DT-08).
    ///
    /// *"⮕ To Nugegoda · 1:42 left"*, **Turn Off**, and the two facts that stop the filter being a
    /// mystery: how many activations are left, and that only direction-matching hires will reach the
    /// driver while it runs. The card takes the warning tint in the last ten minutes, which is the same
    /// threshold notify-svc's `directional.expiring` push uses.
    private var activeFilterCard: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
                Text(
                    "directional_active_to".localisedFormat(
                        model.state.filter?.label ?? MageRideSymbols.unknown,
                        MoneyFormat.countdown(seconds: model.state.timeRemainingSeconds)
                    )
                )
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)

                Spacer(minLength: MageRideSpacing.xs)

                Button { Task { await model.turnOff() } } label: {
                    Text(key: "directional_turn_off")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.primary)
                }
                .buttonStyle(.plain)
                .disabled(model.state.isBusy)
            }

            Text("directional_uses_remaining".localisedFormat(model.state.usesRemaining))
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            Text(key: "directional_active_note")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            model.state.isExpiringSoon
                ? MageRideColor.warning.opacity(DirectionalScreen.expiringTint)
                : MageRideColor.primaryContainer,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .overlay {
            RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                .strokeBorder(MageRideColor.primary, lineWidth: 2)
        }
    }

    /// *"2h"*, or an em dash until the server has told this build a ceiling. See ``DirectionalModel``.
    private var maxDurationText: String {
        model.state.maxDurationSec.map { MoneyFormat.countdown(seconds: $0) } ?? MageRideSymbols.unknown
    }

    /// How much of the warning colour the expiring card keeps.
    private static let expiringTint: Double = 0.22
}
