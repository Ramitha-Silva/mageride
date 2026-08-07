import SwiftUI

/// **SCR-DI-034 · alerts** (Epic 10, D5' §14.4).
///
/// The wireframe: an `Alerts` large title over a grouped list, each row a tinted leading square, a
/// headline and a relative time — *"Directional expiring in 10 min · 2 min ago"*.
///
/// **Shimmer while loading**, which D2' names for this screen: four placeholder rows rather than a
/// spinner, because the list has a shape and a spinner over an empty column reads as an empty inbox.
/// The shimmer is a `.redacted(reason: .placeholder)` group — the platform's own version of the
/// effect, so it respects Reduce Motion without a branch here.
///
/// It hangs off the **Menu** tab, so the system back button says `‹ Menu`.
///
/// - Parameter onOpen: A row's deep link resolved to a screen. Unresolvable links are already `nil`
///   — see ``NotificationsModel``.
@MainActor
struct NotificationsScreen: View {

    @StateObject private var model: NotificationsModel

    private let onOpen: (DriverRoute) -> Void

    init(model: @autoclosure @escaping () -> NotificationsModel, onOpen: @escaping (DriverRoute) -> Void) {
        _model = StateObject(wrappedValue: model())
        self.onOpen = onOpen
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
                if model.state.isLoading {
                    shimmer
                } else if model.state.isEmpty {
                    Text(key: "alerts_empty")
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .padding(.top, MageRideSpacing.lg)
                } else {
                    // ``DriverAlert`` is `Identifiable` on §1.6's own primary key, so `ForEach` needs
                    // no `id:` — and it must not be given one built from `Array(_:enumerated())`,
                    // because **Swift has no key paths into tuples** (the C087 finding).
                    GroupedList {
                        ForEach(model.state.alerts) { alert in
                            row(alert, showsSeparator: alert.id != model.state.alerts.last?.id)
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "alerts_title"))
        .navigationBarTitleDisplayMode(.large)
        .toolbar {
            if model.state.hasUnread {
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button(action: model.markAllRead) {
                        Text(key: "alerts_mark_all_read")
                    }
                }
            }
        }
        .task { await model.refresh() }
        .onChange(of: model.state.opening) { route in
            guard let route else { return }
            model.consumeOpening()
            onOpen(route)
        }
    }

    /// One `.glist .gr` — the tinted square, the headline and the relative time.
    private func row(_ alert: DriverAlert, showsSeparator: Bool) -> some View {
        let kind = AlertKind.of(alert.type)

        return VStack(spacing: 0) {
            Button { model.open(alert) } label: {
                HStack(spacing: MageRideSpacing.sm) {
                    Image(systemName: kind.symbolName)
                        .font(.footnote)
                        .foregroundStyle(kind.accent)
                        .frame(width: MageRideControl.listRowIcon, height: MageRideControl.listRowIcon)
                        .background(
                            kind.accent.opacity(Self.squareTint),
                            in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                        )

                    VStack(alignment: .leading, spacing: 1) {
                        Text(alert.title ?? kind.labelKey.localised)
                            // An unread alert is the one thing on this list a driver is scanning for.
                            .mageFont(alert.isRead ? .body : .bodyEmphasis)
                            .foregroundStyle(MageRideColor.onSurface)
                            .multilineTextAlignment(.leading)

                        if let body = alert.body, !body.isEmpty {
                            Text(body)
                                .mageFont(.caption)
                                .foregroundStyle(MageRideColor.onSurfaceVariant)
                                .multilineTextAlignment(.leading)
                        }

                        Text(AlertAge.of(receivedAt: alert.receivedAt, now: Date()).text)
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.outlineVariant)
                    }

                    Spacer(minLength: MageRideSpacing.xs)
                }
                .padding(.horizontal, MageRideSpacing.sm)
                .padding(.vertical, MageRideSpacing.xs)
                .frame(minHeight: MageRideControl.minimumTapTarget)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityElement(children: .combine)

            if showsSeparator {
                Rectangle()
                    .fill(MageRideColor.surfaceVariant)
                    .frame(height: MageRideControl.hairline)
                    .padding(.leading, MageRideSpacing.sm + MageRideControl.listRowIcon + MageRideSpacing.sm)
            }
        }
    }

    /// D2' §SCR-DI-034's *"shimmer loading"* — the shape that is coming, redacted.
    private var shimmer: some View {
        GroupedList {
            ForEach(0..<Self.shimmerRows, id: \.self) { index in
                row(
                    DriverAlert(
                        id: "placeholder-\(index)",
                        type: "UNKNOWN",
                        title: String(repeating: " ", count: 24),
                        receivedAt: Date()
                    ),
                    showsSeparator: index < Self.shimmerRows - 1
                )
            }
        }
        .redacted(reason: .placeholder)
        .disabled(true)
        .accessibilityHidden(true)
    }

    /// How much of the tone tints a row's leading square. Light enough to read the icon over.
    private static let squareTint: Double = 0.18

    /// The wireframe draws four rows, and so does this.
    private static let shimmerRows = 4
}
