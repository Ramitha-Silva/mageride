import MageRideShared
import PhotosUI
import SwiftUI

/// **SCR-DI-003a · profile setup** — driver identity, and the last screen before Home.
///
/// The wireframe top to bottom: a large "Set up profile" title, the centred avatar with its camera
/// badge and the *required* caption under it, the driver-name field, the two licence capture tiles,
/// the "✦ AI-extracted from licence" card, the ⚑ manual-entry warning, and the "Save & continue"
/// CTA on the grouped background.
///
/// Two fences hold this screen up:
///
/// * **No vehicle** (AL-27). Nothing here asks for one, and the next screen is SCR-DI-007, not the
///   Mode-C wizard.
/// * **The scanner is SCR-DI-005's** (AL-43, C087). A capture tile opens it; it does not photograph
///   anything itself. The profile photo is the exception the wireframe itself makes — an avatar is
///   not a document, so it comes from `PhotosPicker`.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct ProfileSetupScreen: View {

    @StateObject private var model: ProfileSetupModel
    @ObservedObject private var captures: DocumentCaptureCoordinator
    @State private var photoItem: PhotosPickerItem?

    private let onCaptureRequested: () -> Void
    private let onComplete: () -> Void

    init(
        profiles: DriverProfileRepository,
        captures: DocumentCaptureCoordinator,
        onCaptureRequested: @escaping () -> Void,
        onComplete: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: ProfileSetupModel(profiles: profiles, captures: captures))
        self.captures = captures
        self.onCaptureRequested = onCaptureRequested
        self.onComplete = onComplete
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                    profilePhoto

                    LabelledTextField(
                        labelKey: "profile_setup_name_label",
                        value: Binding(get: { model.state.draft.name }, set: model.onNameChanged)
                    )

                    SectionLabel(key: "profile_setup_licence_label")
                    licenceTiles

                    if let extraction = model.state.extraction {
                        extractionCard(extraction)
                    }

                    if model.state.hasOfficerFlag {
                        officerFlagCard
                    }

                    if let errorKey = model.state.errorKey {
                        FormErrorText(messageKey: errorKey)
                    }

                    Button(action: { Task { await model.save() } }) {
                        Text(key: "profile_setup_save")
                    }
                    .buttonStyle(.mageCta(loading: model.state.isBusy))
                    .disabled(!model.state.canSave)
                    .padding(.top, MageRideSpacing.xs)
                }
                .padding(MageRideSpacing.md)
            }
            .background(MageRideColor.surface)
            .navigationTitle(Text(key: "profile_setup_title"))
            .navigationBarTitleDisplayMode(.large)
        }
        // SCR-DI-005 hands the cropped, de-skewed image back through the coordinator. Observed
        // rather than passed as a navigation result, because the route carries no arguments —
        // see `DocumentCaptureCoordinator`.
        .onChange(of: captures.result) { result in
            if let result { model.apply(result) }
        }
        .onChange(of: photoItem) { item in
            Task { await loadPhoto(item) }
        }
        .onChange(of: model.state.isDone) { isDone in
            if isDone { onComplete() }
        }
    }

    // MARK: - Photo (US-2.12)

    /// The wireframe's avatar with the camera badge, and the caption that makes the requirement
    /// plain.
    ///
    /// *"Profile photo · required — shown to passengers"*. The word `required` carries the error
    /// colour because it is the one field whose absence a driver will not otherwise notice — the
    /// CTA simply stays dead.
    private var profilePhoto: some View {
        VStack(spacing: MageRideSpacing.xxs) {
            PhotosPicker(selection: $photoItem, matching: .images, photoLibrary: .shared()) {
                ZStack(alignment: .bottomTrailing) {
                    Circle()
                        .fill(MageRideColor.surfaceVariant)
                        .frame(width: avatarSize, height: avatarSize)
                        .overlay {
                            Circle().strokeBorder(
                                model.state.draft.photo == nil ? MageRideColor.outline : MageRideColor.success,
                                lineWidth: model.state.draft.photo == nil ? 1 : 2
                            )
                        }
                        .overlay {
                            Image(systemName: model.state.draft.photo == nil ? "person.fill" : "checkmark")
                                .font(.system(size: avatarSize / 2.6))
                                .foregroundStyle(MageRideColor.onSurfaceVariant)
                        }

                    Circle()
                        .fill(MageRideColor.primary)
                        .frame(width: badgeSize, height: badgeSize)
                        .overlay {
                            Image(systemName: "camera.fill")
                                .font(.caption)
                                .foregroundStyle(MageRideColor.onPrimary)
                        }
                }
            }
            .accessibilityLabel(Text(key: "profile_setup_photo_action"))

            HStack(spacing: MageRideSpacing.xxs) {
                Text(key: "profile_setup_photo_caption")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Text(key: "profile_setup_photo_required")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.error)
            }
            .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
    }

    /// Reads the picked image into memory.
    ///
    /// Bytes rather than the picker's item: the `PhotosPickerItem` is valid for the session that
    /// produced it, and Profile Setup outlives several of those. `capturedVia: .gallery` is AL-43's
    /// provenance and is what tells the Verification-Officer queue this one did not come from the
    /// scanner.
    private func loadPhoto(_ item: PhotosPickerItem?) async {
        guard
            let item,
            let data = try? await item.loadTransferable(type: Data.self)
        else { return }

        model.onPhotoPicked(
            CapturedImage(
                fileName: "profile-photo.jpg",
                data: data,
                capturedVia: CaptureSource.gallery
            )
        )
    }

    // MARK: - Licence (AL-43, US-2.4)

    private var licenceTiles: some View {
        HStack(spacing: MageRideSpacing.xs) {
            CaptureTile(
                labelKey: "profile_setup_licence_front",
                isCaptured: model.state.draft.licenceFront != nil,
                onTap: { capture(.licenceFront) }
            )
            CaptureTile(
                labelKey: "profile_setup_licence_back",
                isCaptured: model.state.draft.licenceBack != nil,
                onTap: { capture(.licenceBack) }
            )
        }
    }

    private func capture(_ target: DocumentCaptureTarget) {
        model.requestCapture(target)
        onCaptureRequested()
    }

    // MARK: - The extract card (AL-29, BR-25.2)

    /// The wireframe's "✦ AI-extracted from licence (Gemini Flash · PII redacted)" card.
    ///
    /// All four rows carry a ✎, and the two the driver can type into without one — the NIC and the
    /// licence classes — get their own controls underneath when the scan did not settle them. The
    /// client never stamps a provenance: registry-svc writes `source='manual'` and
    /// `verify_status='pending'`, because a client that could claim `source='ai'` would make AL-29
    /// advisory.
    private func extractionCard(_ extraction: LicenceExtraction) -> some View {
        NoticeCard(
            titleKey: "profile_setup_extract_title",
            symbolName: "sparkles",
            accent: MageRideColor.success
        ) {
            ForEach(extraction.fields) { field in
                ExtractedFieldRow(
                    labelKey: Self.labelKey(for: field.key),
                    value: typedValue(for: field.key) ?? field.value,
                    isFlagged: field.needsOfficerReview,
                    onEdit: Self.isEditable(field.key) ? { model.toggleEdit(field.key) } : nil
                )

                if model.state.editingKey == field.key {
                    LabelledTextField(
                        labelKey: Self.labelKey(for: field.key),
                        value: Binding(
                            get: { typedValue(for: field.key) ?? field.value ?? "" },
                            set: { model.onLicenceFieldChanged(key: field.key, value: $0) }
                        ),
                        supportingKey: "profile_setup_manual_supporting"
                    )
                }
            }

            // The two values that appear only when the scan did not settle them, which is
            // BR-25.2's "if a value is unreadable the driver types it".
            if extraction.field(LicenceFieldKeys.nicNo)?.needsOfficerReview != false {
                LabelledTextField(
                    labelKey: "profile_setup_nic_label",
                    value: Binding(get: { model.state.draft.nicNo ?? "" }, set: model.onNicChanged),
                    supportingKey: "profile_setup_manual_supporting"
                )
            }

            if extraction.field(LicenceFieldKeys.allowedVehicleTypes)?.needsOfficerReview != false {
                SectionLabel(key: "profile_setup_allowed_types_label")
                allowedVehicleTypes
            }
        }
    }

    /// The licence classes a driver types in when the scan could not read them.
    ///
    /// Toggle chips over the **canonical vehicle types** (AL-09) rather than free text, because
    /// `allowedVehicleTypes` is a `VehicleType[]` on the wire — a driver typing `B1` would produce a
    /// value `400 validation-failed` rejects. `bus` and `train` are absent: they are Mode A, and
    /// Mode A vehicles are the Fleet Portal's (AL-27).
    private var allowedVehicleTypes: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: MageRideSpacing.xs) {
                ForEach(Self.rideBookableTypes, id: \.wire) { type in
                    let selected = model.state.draft.allowedVehicleTypes?.contains(type) == true
                    Button(action: { toggle(type) }) {
                        Text(key: Self.labelKey(forVehicle: type))
                            .mageFont(.bodySmall)
                            .foregroundStyle(selected ? MageRideColor.onPrimary : MageRideColor.onSurface)
                            .padding(.horizontal, MageRideSpacing.sm)
                            .padding(.vertical, MageRideSpacing.xs)
                            .background(
                                selected ? MageRideColor.primary : MageRideColor.background,
                                in: Capsule()
                            )
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
                }
            }
            .padding(.vertical, 2)
        }
    }

    private func toggle(_ type: VehicleType) {
        var selected = model.state.draft.allowedVehicleTypes ?? []
        if let index = selected.firstIndex(of: type) {
            selected.remove(at: index)
        } else {
            selected.append(type)
        }
        model.onAllowedVehicleTypesChanged(selected)
    }

    /// The ⚑ banner. It says who will look at the value, not that anything is wrong.
    private var officerFlagCard: some View {
        NoticeCard(
            titleKey: "profile_setup_manual_title",
            symbolName: "exclamationmark.triangle.fill",
            accent: MageRideColor.warning,
            fill: MageRideColor.warning.opacity(0.12)
        ) {
            HStack(alignment: .top, spacing: MageRideSpacing.xs) {
                Text(key: "profile_setup_manual_body")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
                Spacer(minLength: 0)
                AdminVerifyChip()
            }
        }
    }

    // MARK: -

    private func typedValue(for key: String) -> String? {
        switch key {
        case LicenceFieldKeys.licenceNo: return model.state.draft.licenceNo
        case LicenceFieldKeys.licenceExpiry: return model.state.draft.licenceExpiry
        default: return nil
        }
    }

    /// The two rows a driver can correct in place. NIC and the licence classes have their own
    /// controls below the card, because one is a free-text field and the other is a chip set.
    private static func isEditable(_ key: String) -> Bool {
        key == LicenceFieldKeys.licenceNo || key == LicenceFieldKeys.licenceExpiry
    }

    /// The trilingual row label for one `registry.document_fields` key.
    private static func labelKey(for key: String) -> String {
        switch key {
        case LicenceFieldKeys.licenceNo: return "profile_setup_field_licence_no"
        case LicenceFieldKeys.licenceExpiry: return "profile_setup_field_expiry"
        case LicenceFieldKeys.nicNo: return "profile_setup_field_nic"
        default: return "profile_setup_field_allowed_types"
        }
    }

    /// AL-09's eight ride-bookable types, which is `VehicleType` minus `bus` and `train`.
    ///
    /// Written out rather than filtered off `VehicleType.values()`: the wireframe's chip order is
    /// the tier ladder's (motorbike through mini truck), and a `values()` order is the wire enum's.
    /// `isRideBookable` still holds for every entry — the two are checked against each other in
    /// `ProfileSetupModelTests`.
    private static let rideBookableTypes: [VehicleType] = [
        VehicleType.motorbike,
        VehicleType.threeWheeler,
        VehicleType.flex,
        VehicleType.sedan,
        VehicleType.miniVan,
        VehicleType.van,
        VehicleType.truck,
        VehicleType.miniTruck,
    ]

    private static func labelKey(forVehicle type: VehicleType) -> String {
        "vehicle_type_" + type.wire
    }

    private let avatarSize: CGFloat = 84
    private let badgeSize: CGFloat = 28
}
