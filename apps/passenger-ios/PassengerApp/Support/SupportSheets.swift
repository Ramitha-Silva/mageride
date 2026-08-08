import MageRideShared
import PhotosUI
import SwiftUI

/// SCR-PI-030's two overlays, hosted together because at most one is ever up.
///
/// The raise-ticket sheet is the wireframe's own **SCR-PI-030a**, and its states line fixes the
/// presentation: *"iOS `.sheet` (detent `.medium`)"*. The thread sheet is D2' §SCR-PI-030's *"ticket
/// list/thread"*, which the wireframe draws no separate frame for — so it is a `.sheet` over the
/// list the passenger tapped rather than a destination the team-approved baseline has no picture of.
/// `apps/passenger-android` makes the same call with a `ModalBottomSheet`.
@MainActor
struct SupportSheets: View {

    let sheet: SupportSheet
    @ObservedObject var model: SupportModel

    var body: some View {
        switch sheet {
        case .raiseTicket:
            RaiseTicketSheet(model: model)
        case .ticketThread:
            TicketThreadSheet(ticket: model.state.ticket)
        }
    }
}

/// **SCR-PI-030a · raise a ticket** — a `.sheet` at detent `.medium` (US-16.2).
///
/// The wireframe's four controls, in its order: **Issue description** (the `min-height:72px` box),
/// the **Related trip** field with its `▾`, *"📎 Attach a screenshot"* and **Submit ticket**.
///
/// `.large` is offered beside `.medium` for ``LabelledTextField``'s reason on SCR-PI-026a: a
/// three-line description at an accessibility content size does not fit half a screen, and a second
/// detent is what stops the CTA sitting under the keyboard.
@MainActor
private struct RaiseTicketSheet: View {

    @ObservedObject var model: SupportModel

    /// The system photo picker: **no `NSPhotoLibraryUsageDescription`**, because `PhotosPicker` is
    /// PHPicker and runs out of process, granting access to the one image the passenger chose. The
    /// same contract C100's transfer slip uses, and the reason this app's `Info.plist` carries no
    /// photo-library purpose string.
    @State private var picked: PhotosPickerItem?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                Text(key: "support_raise_ticket")
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)

                MultilineTextField(
                    labelKey: "support_issue_label",
                    placeholderKey: "support_issue_hint",
                    value: Binding(get: { model.state.description }, set: model.onDescriptionChange)
                )

                SectionLabel(key: "support_related_trip")
                tripPicker

                attachButton

                Button { Task { await model.submit() } } label: {
                    Text(key: "support_submit_ticket")
                }
                .buttonStyle(.mageCta(loading: model.state.isSubmitting))
                .disabled(!model.state.canSubmit)

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.background)
        .presentationDetents([.medium, .large])
        .onChange(of: picked) { item in
            Task { await load(item) }
        }
    }

    /// The wireframe's *"PAX-90431-0617 · Nugegoda → Galle Face ▾"*.
    ///
    /// **Δ iOS — a `Picker`, which is what the cell's own states line spells out** (*"past **Trip
    /// ID** `Picker`"*) where the Android twin builds an `ExposedDropdownMenuBox`. `.menu` style, so
    /// it reads as the wireframe's `▾` field rather than as a wheel; what it shows is the day and the
    /// route, for ``SupportLabels/trip(_:)``'s reason.
    ///
    /// Optional, and *"No trip"* is a real option rather than a placeholder: a complaint about the
    /// app itself is not about a trip at all.
    private var tripPicker: some View {
        Picker(
            selection: Binding(get: { model.state.tripId }, set: model.onTripSelected),
            label: Text(key: "support_related_trip")
        ) {
            Text(key: "support_trip_none").tag(String?.none)
            ForEach(model.state.trips, id: \.rideId) { trip in
                Text(SupportLabels.trip(trip)).tag(String?.some(trip.rideId))
            }
        }
        .pickerStyle(.menu)
        .labelsHidden()
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, MageRideSpacing.sm)
        .frame(minHeight: MageRideControl.minimumTapTarget)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    /// *"📎 Attach a screenshot"*, and what it says once one is attached.
    ///
    /// Drawn as an ``OutlinedAction`` rather than being one: `PhotosPicker`'s label is its own
    /// trigger, so a button wrapping it would be a control inside a control.
    private var attachButton: some View {
        PhotosPicker(selection: $picked, matching: .images, photoLibrary: .shared()) {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: model.state.screenshotName == nil ? "paperclip" : "checkmark.circle.fill")
                    .font(.footnote)
                Text(key: model.state.screenshotName == nil ? "support_attach" : "support_attached")
                    .mageFont(.bodySmall)
            }
            .foregroundStyle(model.state.screenshotName == nil ? MageRideColor.primary : MageRideColor.success)
            .frame(maxWidth: .infinity, minHeight: MageRideControl.outlinedAction)
            .background(
                MageRideColor.background,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(MageRideColor.outline, lineWidth: MageRideControl.hairline * 2)
            }
            .contentShape(Rectangle())
        }
    }

    /// Reads the picked image into memory.
    ///
    /// **Bytes, not the picker's item.** A `PhotosPickerItem` is valid for the session that produced
    /// it and this one has to survive until Submit — the same reason `apps/passenger-android`'s
    /// `PickedScreenshot` reads the `Uri` at the pick rather than parking it in state. The name is
    /// the app's own and not the file's: `IObjectStore`'s key is built from ids the platform minted,
    /// never from a client filename (backend/CLAUDE.md).
    private func load(_ item: PhotosPickerItem?) async {
        guard let item, let data = try? await item.loadTransferable(type: Data.self) else { return }
        model.onScreenshotPicked(fileName: Self.screenshotFileName, data: data)
    }

    private static let screenshotFileName = "support-screenshot.jpg"
}

/// One ticket and its whole conversation (US-16.2).
@MainActor
private struct TicketThreadSheet: View {

    let ticket: TicketDetail?

    var body: some View {
        ScrollView {
            Group {
                if let ticket {
                    thread(of: ticket)
                } else {
                    LoadingRow(messageKey: "support_loading")
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.background)
        .presentationDetents([.height(MageRideControl.ticketSheetHeight), .large])
    }

    private func thread(of ticket: TicketDetail) -> some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            HStack(spacing: MageRideSpacing.xs) {
                Text(SupportLabels.category(ticket.category))
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                Spacer(minLength: MageRideSpacing.xs)
                StatusPill(titleKey: SupportLabels.statusKey(ticket.status), tone: SupportLabels.tone(ticket.status))
            }

            // `IosTicketKt.ticketDescription`, not `ticket.description`: that name is `NSObject`'s on
            // this bridge and the Kotlin one is mangled around it — see the helper's own note. It
            // was added by C093 for `apps/driver-ios` and is `:shared`'s, so this app reuses it
            // rather than adding a second `iosMain` function with the same body.
            Text(IosTicketKt.ticketDescription(ticket: ticket))
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)

            // Oldest first, as the contract sends it: a thread read bottom-up is a thread nobody
            // reads. `assigned` entries are skipped — who is handling a complaint is not the
            // complainant's to see, which is the contract's own rule rather than this screen's.
            //
            // Keyed on the position, because a `TicketEvent` carries no id and two `responded`
            // entries a minute apart are genuinely two rows. Indices rather than
            // `Array(_:enumerated())`: **Swift has no key paths into tuples** (the C087 finding).
            ForEach(ticket.thread.indices, id: \.self) { index in
                threadEntry(ticket.thread[index])
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// One `TicketEvent` — what happened, and the agent's words when there are any.
    @ViewBuilder
    private func threadEntry(_ event: TicketEvent) -> some View {
        if let labelKey = SupportLabels.eventKey(event.kind) {
            VStack(alignment: .leading, spacing: 1) {
                Text(key: labelKey)
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                if let body = event.body, !body.isEmpty {
                    Text(body)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}
