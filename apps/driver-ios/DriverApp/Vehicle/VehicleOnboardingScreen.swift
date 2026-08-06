import MageRideShared
import SwiftUI

/// **SCR-DI-004 → 004c · vehicle onboarding** — the optional Mode-C four-step wizard.
///
/// One route and one view for all four steps, because that is what they are: the wireframe draws the
/// same navigation bar, the same progress bar and the same CTA on each, with a different body between
/// them. Four destinations would put the resume rule (AL-30) in the navigation graph instead of in
/// ``VehicleOnboardingModel``, where it can be tested.
///
/// **There is no permit field and no GPS-tracker field on any of the four.** Mode A/B vehicles and
/// route permits are onboarded in the Fleet Portal (AL-27, SCR-FP-004) and the wizard says so, in the
/// notice card the wireframe puts on Step 1/4.
///
/// - Parameters:
///   - onCaptureRequested: Opens SCR-DI-005 — the tiles here never photograph anything.
///   - onSubmitted: Step 4/4 is saved; hand over to SCR-DI-006.
///   - onExit: Back from Step 1/4 leaves the wizard entirely.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct VehicleOnboardingScreen: View {

    @StateObject private var model: VehicleOnboardingModel
    @ObservedObject private var captures: DocumentCaptureCoordinator

    private let onCaptureRequested: () -> Void
    private let onSubmitted: () -> Void
    private let onExit: () -> Void

    init(
        vehicles: VehicleOnboardingRepository,
        captures: DocumentCaptureCoordinator,
        session: VehicleOnboardingSession,
        onCaptureRequested: @escaping () -> Void,
        onSubmitted: @escaping () -> Void,
        onExit: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: VehicleOnboardingModel(vehicles: vehicles, captures: captures, session: session)
        )
        self.captures = captures
        self.onCaptureRequested = onCaptureRequested
        self.onSubmitted = onSubmitted
        self.onExit = onExit
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                if model.state.isLoading {
                    ProgressView()
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, MageRideSpacing.xl)
                } else {
                    StepProgress(
                        step: model.state.stepNumber,
                        count: VehicleOnboardingSteps.count,
                        captionKey: model.state.step.captionKey
                    )

                    stepBody

                    if model.state.isPendingReview {
                        pendingReviewCard
                    }
                }

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                Button(action: { Task { await model.onContinue() } }) {
                    Text(key: model.state.step.ctaKey)
                }
                .buttonStyle(.mageCta(loading: model.state.isBusy))
                .disabled(!model.state.canContinue)
                .padding(.top, MageRideSpacing.xs)
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: model.state.step.titleKey))
        .navigationBarTitleDisplayMode(.inline)
        // The wizard's Back is the model's — from Step 1/4 it leaves, and from anywhere else it is a
        // step backwards (D2' §SCR-DI-004). The system's own back button would pop the whole screen
        // from Step 3.
        .navigationBarBackButtonHidden(true)
        .toolbar {
            ToolbarItem(placement: .navigationBarLeading) { backButton }
            ToolbarItem(placement: .navigationBarTrailing) { stepBadge }
        }
        .task { await model.load() }
        // SCR-DI-005 hands the de-skewed image back through the coordinator. Observed rather than
        // passed as a navigation result, because the route carries no arguments.
        .onChange(of: captures.result) { result in
            if let result { model.apply(result) }
        }
        .onChange(of: model.state.isSubmitted) { isSubmitted in
            if isSubmitted { onSubmitted() }
        }
        .onChange(of: model.state.hasExited) { hasExited in
            if hasExited { onExit() }
        }
    }

    // MARK: - The bar

    /// The wireframe's `‹ Cancel` on Step 1/4 and `‹ Back` on the other three — which is the same
    /// distinction the model draws, spelled where the driver can read it before they tap.
    private var backButton: some View {
        Button(action: model.onBack) {
            HStack(spacing: 2) {
                Image(systemName: "chevron.left")
                Text(key: model.state.step == OnboardingStep.details ? "action_cancel" : "action_back")
                    .mageFont(.body)
            }
        }
    }

    /// `Mode C` on Step 1/4, and the wireframe's `2/4` counter afterwards.
    ///
    /// The counter is composed rather than translated: it is two numbers and a solidus, which is data
    /// in every language — the same rule that keeps `+94` and the language endonyms out of the
    /// strings files. The trilingual form of the same fact is the caption under the progress bar.
    @ViewBuilder
    private var stepBadge: some View {
        if model.state.step == OnboardingStep.details {
            ModeCBadge()
        } else {
            Text("\(model.state.stepNumber)/\(VehicleOnboardingSteps.count)")
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
    }

    // MARK: - The four bodies

    @ViewBuilder
    private var stepBody: some View {
        switch model.state.step {
        case OnboardingStep.insurance:
            documentStep(
                target: .insurance,
                slotLabelKey: "vehicle_onboard_insurance_slot",
                captionKey: "vehicle_onboard_insurance_caption"
            )

        case OnboardingStep.revenue:
            documentStep(
                target: .revenueLicence,
                slotLabelKey: "vehicle_onboard_revenue_slot",
                captionKey: "vehicle_onboard_revenue_caption"
            )

        case OnboardingStep.photos:
            photosStep

        // `details`, plus the arm a Kotlin enum forces on every Swift `switch` over one.
        default:
            detailsStep
        }
    }

    /// **SCR-DI-004 · Step 1/4** — vehicle type and registration number, and nothing else.
    ///
    /// The notice card is not decoration. It is the one place a Mode-C driver is told where a permit
    /// and a Mode A/B vehicle go, and the absence of those two fields is the AL-27 fence itself.
    private var detailsStep: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            GroupedList {
                GroupedRow(titleKey: "vehicle_onboard_type_label") {
                    vehicleTypeMenu
                }
                GroupedRow(titleKey: "vehicle_onboard_registration_label", showsSeparator: false) {
                    TextField(
                        "",
                        text: Binding(
                            get: { model.state.registrationNumber },
                            set: model.onRegistrationChanged
                        )
                    )
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                    .multilineTextAlignment(.trailing)
                    .textInputAutocapitalization(.characters)
                    .autocorrectionDisabled()
                    .accessibilityLabel(Text(key: "vehicle_onboard_registration_label"))
                }
            }

            if model.state.isRegistrationTaken {
                FormErrorText(messageKey: "error_registration_exists")
            }

            NoticeCard(
                titleKey: "vehicle_onboard_mode_c_title",
                symbolName: "info.circle.fill",
                accent: MageRideVehicleColor.modeC
            ) {
                Text(key: "vehicle_onboard_mode_c_body")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
            }
        }
    }

    /// The Mode-C subset (AL-09), as the wireframe's `Sedan ›` row.
    ///
    /// A `Menu` rather than the Android screen's dropdown text field, because `driver_ios.html` draws
    /// this as a grouped-list row with a chevron and D2' §C maps that onto `Form`. `bus` and `train`
    /// are absent by construction — the values are `RideVehicleType`s, and `POST /v1/vehicles`
    /// answers `403 mode-not-allowed` for either.
    private var vehicleTypeMenu: some View {
        Menu {
            ForEach(Self.modeCTypes, id: \.wire) { type in
                Button(action: { model.onVehicleTypeChanged(type) }) {
                    Text(key: type.labelKey)
                }
            }
        } label: {
            HStack(spacing: 2) {
                Text(model.state.vehicleType.map { $0.labelKey.localised } ?? Self.unset)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                Image(systemName: "chevron.right")
                    .font(.footnote)
                    .foregroundStyle(MageRideColor.outline)
            }
        }
        .accessibilityLabel(Text(key: "vehicle_onboard_type_label"))
    }

    /// **SCR-DI-004a / 004b · Steps 2 and 3** — one capture, one `Done ✓`, one extract card.
    ///
    /// The two steps differ only in what they capture and which fields come back, so they are one
    /// view. The extract card appears only after a save, because the save is what queues the Gemini
    /// Flash extraction — see ``VehicleOnboardingModel``.
    private func documentStep(
        target: DocumentCaptureTarget,
        slotLabelKey: String,
        captionKey: String
    ) -> some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            CaptureTile(
                labelKey: captionKey,
                isCaptured: model.state.isCaptured(target),
                height: MageRideControl.capturePanel,
                onTap: { capture(target) }
            )

            GroupedList {
                GroupedRow(titleKey: slotLabelKey, showsSeparator: false) {
                    StatusPill.captured(model.state.isCaptured(target))
                }
            }

            extractionCard(isEditable: true)
        }
    }

    /// **SCR-DI-004c · Step 4/4** — front and back, number plate visible on both.
    ///
    /// The notice under the tiles is the step's whole rule: the plate read out of these photos is
    /// matched against the registration number typed on Step 1/4, and a mismatch is what makes the
    /// step Pending (BR-25.3, US-2.27).
    private var photosStep: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            HStack(spacing: MageRideSpacing.xs) {
                CaptureTile(
                    labelKey: "vehicle_onboard_photo_front",
                    isCaptured: model.state.photoFront != nil,
                    height: MageRideControl.capturePanel,
                    onTap: { capture(.vehicleFront) }
                )
                CaptureTile(
                    labelKey: "vehicle_onboard_photo_back",
                    isCaptured: model.state.photoBack != nil,
                    height: MageRideControl.capturePanel,
                    onTap: { capture(.vehicleBack) }
                )
            }

            GroupedList {
                GroupedRow(titleKey: "vehicle_onboard_photos_slot", showsSeparator: false) {
                    StatusPill.captured(model.state.photoFront != nil && model.state.photoBack != nil)
                }
            }

            NoticeCard(symbolName: "info.circle.fill", accent: MageRideVehicleColor.modeC) {
                Text("vehicle_onboard_plate_match".localisedFormat(model.state.registrationNumber))
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
            }

            // Not editable: Step 4/4's two rows are the plate check itself and are never the
            // driver's to retype — see ``VehicleFieldKeys/correctable``.
            extractionCard(isEditable: false)
        }
    }

    private func capture(_ target: DocumentCaptureTarget) {
        model.requestCapture(target)
        onCaptureRequested()
    }

    // MARK: - The extract card

    /// The wireframe's *"✦ AI-extracted (Gemini Flash 3.0)"* card, with the ✎ it draws.
    ///
    /// **Δ MCS-02 — the rows are editable.** `OnboardingCorrections` carries the driver's value and
    /// the document already on record is corrected in place, with no second photograph. Only the keys
    /// the step's document kind accepts get a ✎: `plate_text` and `reg_no_match` are **not** among
    /// them, because they are the fraud check Step 4/4 exists to perform and a driver who could
    /// retype the plate the camera read would be verifying their own vehicle.
    @ViewBuilder
    private func extractionCard(isEditable: Bool) -> some View {
        let fields = model.state.stepFields
        if !fields.isEmpty {
            NoticeCard(
                titleKey: "vehicle_onboard_extract_title",
                symbolName: "sparkles",
                accent: MageRideColor.success
            ) {
                ForEach(fields, id: \.key) { field in
                    extractedRow(field, isEditable: isEditable)
                }

                // The wireframe's `Confidence · 0.62 — doubtful` row. "Doubtful" is read off the
                // server's own `verifyStatus` rather than compared against a threshold here:
                // `Registry:OcrConfidenceThreshold` is admin-tunable and a second copy of it in the
                // app would eventually disagree with the verdict printed beside it.
                if let confidence = model.state.lowestConfidence {
                    ExtractedFieldRow(
                        labelKey: "vehicle_field_confidence",
                        value: fields.contains(where: \.needsOfficerReview)
                            ? "vehicle_confidence_doubtful".localisedFormat(formattedConfidence(confidence))
                            : formattedConfidence(confidence),
                        isFlagged: false
                    )
                }
            }
        }
    }

    @ViewBuilder
    private func extractedRow(_ field: ExtractedField, isEditable: Bool) -> some View {
        let canEdit = isEditable && VehicleFieldKeys.correctable.contains(field.key)

        ExtractedFieldRow(
            labelKey: field.labelKey,
            value: model.state.corrections[field.key] ?? field.displayValue,
            isFlagged: field.needsOfficerReview,
            onEdit: canEdit ? { model.toggleEdit(field.key) } : nil
        )

        if model.state.editingKey == field.key, canEdit {
            LabelledTextField(
                labelKey: field.labelKey,
                value: Binding(
                    get: { model.state.corrections[field.key] ?? field.value ?? "" },
                    set: { model.onCorrectionChanged(key: field.key, value: $0) }
                ),
                supportingKey: "vehicle_onboard_correction_supporting"
            )
        }
    }

    /// The wireframe's amber *"Any element doubtful or edited → this step's status is Pending"* card.
    private var pendingReviewCard: some View {
        NoticeCard(
            titleKey: "vehicle_onboard_pending_title",
            symbolName: "exclamationmark.triangle.fill",
            accent: MageRideColor.warning,
            fill: MageRideColor.warning.opacity(0.12)
        ) {
            HStack(alignment: .top, spacing: MageRideSpacing.xs) {
                Text(key: "vehicle_onboard_pending_body")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
                Spacer(minLength: 0)
                AdminVerifyChip()
            }
        }
    }

    // MARK: -

    /// AL-09's eight ride-bookable types, in the wireframe's own order (the tier ladder, motorbike
    /// through mini truck) rather than the wire enum's.
    ///
    /// **The wireframe's caption under this row lists ten**, `bus` and `train` included, and that
    /// line is deliberately not rendered: those two are Mode A, `POST /v1/vehicles` answers
    /// `403 mode-not-allowed` for either, and offering one would be a control that can only fail. The
    /// Android screen offers the same eight. See the C087 handoff.
    private static let modeCTypes: [RideVehicleType] = [
        RideVehicleType.motorbike,
        RideVehicleType.threeWheeler,
        RideVehicleType.flex,
        RideVehicleType.sedan,
        RideVehicleType.miniVan,
        RideVehicleType.van,
        RideVehicleType.truck,
        RideVehicleType.miniTruck,
    ]

    /// What the type row shows before a type is chosen. An em dash rather than a string key, for
    /// `LocalizationTests`' own reason: a glyph is the same in all three scripts and three identical
    /// values in three strings files is exactly what that test (correctly) fails on.
    private static let unset = "—"
}
