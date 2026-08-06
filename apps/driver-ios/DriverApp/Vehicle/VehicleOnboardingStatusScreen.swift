import MageRideShared
import SwiftUI

/// **SCR-DI-006 · vehicle onboarding status** — the four-document verdict list.
///
/// The wireframe top to bottom: the ⏳ disc with *"Sedan · ABC-1234"* and *"Gemini Flash 3.0
/// verifying…"* beside it, the `Document verification (4)` card with one Verified/Pending row per
/// document, the amber banner counting what is pending, and the note that all four Verified
/// auto-approves the vehicle with no manual step.
///
/// - Parameters:
///   - onDone: Leaves for My Vehicles — where an approved vehicle now appears (SCR-DI-026).
///   - onResume: Opens the wizard again at whatever step is still outstanding (AL-30).
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct VehicleOnboardingStatusScreen: View {

    @StateObject private var model: VehicleOnboardingStatusModel

    private let onDone: () -> Void
    private let onResume: () -> Void

    init(
        vehicles: VehicleOnboardingRepository,
        session: VehicleOnboardingSession,
        onDone: @escaping () -> Void,
        onResume: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: VehicleOnboardingStatusModel(vehicles: vehicles, session: session))
        self.onDone = onDone
        self.onResume = onResume
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                content

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                // A step that is not yet *saved* is the driver's to finish; one that is saved and
                // pending is an officer's. Only the first gets a Resume (AL-30, US-2.10).
                if model.state.canResume {
                    Button(action: onResume) {
                        Text(key: "vehicle_status_resume")
                    }
                    .buttonStyle(.mageCta)
                    .padding(.top, MageRideSpacing.xs)
                }

                Button(action: onDone) {
                    Text(key: "vehicle_status_my_vehicles")
                }
                .buttonStyle(.mageCtaTonal)
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(
            Text(key: model.state.isApproved ? "vehicle_status_approved_title" : "vehicle_status_title")
        )
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                // A Pending document is confirmed by an officer minutes or days later, and US-2.14's
                // push is what brings the driver back. This is how they look again.
                Button(action: { Task { await model.refresh() } }) {
                    Image(systemName: "arrow.clockwise")
                }
                .disabled(model.state.isLoading)
                .accessibilityLabel(Text(key: "action_retry"))
            }
        }
        .task { await model.refresh() }
    }

    @ViewBuilder
    private var content: some View {
        if model.state.isLoading, model.state.verdicts == nil {
            ProgressView()
                .frame(maxWidth: .infinity)
                .padding(.vertical, MageRideSpacing.xl)
        } else if model.state.isUnknownVehicle {
            Text(key: "vehicle_status_unknown")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        } else {
            header
            verdictCard

            if model.state.pendingCount > 0 {
                NoticeCard(symbolName: "exclamationmark.triangle.fill", accent: MageRideColor.warning) {
                    Text("vehicle_status_pending_banner".localisedFormat(model.state.pendingCount))
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurface)
                }
            }

            if model.state.isRejected, let reason = model.state.rejectionReason {
                NoticeCard(
                    titleKey: "vehicle_status_rejected_title",
                    symbolName: "exclamationmark.triangle.fill",
                    accent: MageRideColor.error
                ) {
                    // The reason is an operator's free text, not a `ProblemDetails` code — D-26 is
                    // about resolving an error *code* to copy, and there is no code here to resolve.
                    Text(reason)
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurface)
                }
            }

            NoticeCard(symbolName: "info.circle.fill", accent: MageRideColor.success) {
                Text(key: "vehicle_status_auto_approve")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
            }
        }
    }

    /// The wireframe's ⏳ disc, the *"Sedan · ABC-1234"* line, and the verifying caption under it.
    private var header: some View {
        HStack(spacing: MageRideSpacing.sm) {
            let approved = model.state.isApproved
            let accent = approved ? MageRideColor.success : MageRideColor.warning

            Circle()
                .fill(accent.opacity(Self.discTint))
                .frame(width: MageRideControl.statusAvatar, height: MageRideControl.statusAvatar)
                .overlay {
                    Image(systemName: approved ? "checkmark.circle.fill" : "hourglass")
                        .font(.system(size: MageRideControl.illustrationIcon / 2))
                        .foregroundStyle(accent)
                }
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
                Text(model.state.headerText)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                Text(key: approved ? "vehicle_status_approved_caption" : "vehicle_status_verifying")
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
    }

    /// The wireframe's `Document verification (4)` card — one row per document, Verified or Pending.
    private var verdictCard: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            Text("vehicle_status_documents".localisedFormat(model.state.rows.count))
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            ForEach(model.state.rows) { row in
                HStack(spacing: MageRideSpacing.xs) {
                    Text(key: row.step.documentKey)
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurface)
                    Spacer(minLength: MageRideSpacing.xs)
                    StatusPill(label: row.verdict.labelKey.localised, tone: row.verdict.tone)
                }
                .accessibilityElement(children: .combine)
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    /// How much of the accent the ⏳ disc keeps — the wireframe's `#FFF1D6` behind an amber glyph.
    private static let discTint: Double = 0.18
}
