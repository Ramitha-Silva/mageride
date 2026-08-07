import MageRideShared
import PhotosUI
import SwiftUI

/// SCR-DI-033's three overlays, hosted together because at most one is ever up.
///
/// The raise-ticket sheet is the wireframe's own **SCR-DI-033a**; the article and thread sheets are
/// US-16.1's *"+ detail"* and US-16.2's *"track ticket"*, which D2' names and the wireframe draws no
/// frame for. Both are `.sheet` rather than routes for that reason — adding two destinations the
/// team-approved baseline has no cell for would be a deviation, and a sheet over the list the driver
/// tapped is the least of it. All three take `.presentationDetents([.medium])`, which is D2'
/// §SCR-DI-033a's own SwiftUI clause.
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
        case .faqArticle:
            ArticleSheet(article: model.state.article)
        }
    }
}

/// **SCR-DI-033a · raise a ticket** — a `.sheet` at detent `.medium` (US-16.2).
///
/// The wireframe's four controls, in its order: **Issue description**, the **Related trip** picker,
/// *"📎 Attach a screenshot"* and **Submit ticket**. The title changes with the category — the same
/// sheet is the daily-fee refund request (US-9.23) — because both post the same operation and only
/// `category` differs.
@MainActor
private struct RaiseTicketSheet: View {

    @ObservedObject var model: SupportModel

    /// The system photo picker: **no `NSPhotoLibraryUsageDescription`**, because `PhotosPicker` is
    /// PHPicker and runs out of process, granting access to the one image the driver chose. The same
    /// contract SCR-DI-005's gallery fallback uses, and the same reason `Info.plist` deliberately
    /// carries no photo-library purpose string.
    @State private var picked: PhotosPickerItem?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                Text(key: model.state.isRefundRequest ? "support_refund_title" : "support_raise_ticket")
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

    /// The wireframe's *"DRV-22011-0617 · Galle Face → Nugegoda ▾"*.
    ///
    /// **Δ iOS — a `Picker`, which is what D2' §SCR-DI-033a spells out** (*"past Trip ID `Picker`"*)
    /// where the Android twin builds an `ExposedDropdownMenuBox`. `.menu` style, so it reads as the
    /// wireframe's `▾` field rather than as a wheel; the id it shows is the route and the day, for
    /// ``SupportLabels/trip(_:)``'s reason.
    ///
    /// Optional, and *"No trip"* is a real option rather than a placeholder: a fee charged when the
    /// app crashed on Go Online is not about a trip at all, which is the commonest ticket this sheet
    /// opens.
    private var tripPicker: some View {
        Picker(
            selection: Binding(get: { model.state.tripId }, set: model.onTripSelected),
            label: Text(key: "support_related_trip")
        ) {
            Text(key: "support_trip_none").tag(String?.none)
            ForEach(model.state.trips, id: \.tripId) { trip in
                Text(SupportLabels.trip(trip)).tag(String?.some(trip.tripId))
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
    private var attachButton: some View {
        PhotosPicker(selection: $picked, matching: .images, photoLibrary: .shared()) {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: model.state.screenshot == nil ? "paperclip" : "checkmark.circle.fill")
                    .font(.footnote)
                Text(key: model.state.screenshot == nil ? "support_attach" : "support_attached")
                    .mageFont(.subtitle)
            }
            .foregroundStyle(model.state.screenshot == nil ? MageRideColor.primary : MageRideColor.success)
            .frame(maxWidth: .infinity, minHeight: MageRideControl.ctaHeight)
            .overlay {
                RoundedRectangle(cornerRadius: MageRideControl.ctaRadius, style: .continuous)
                    .strokeBorder(MageRideColor.outline, lineWidth: 1)
            }
            .contentShape(Rectangle())
        }
    }

    /// Reads the picked image into memory.
    ///
    /// Bytes rather than the picker's item, for ``CapturedImage``'s own reason: a `PhotosPickerItem`
    /// is valid for the session that produced it, and this one has to survive until Submit.
    ///
    /// **`CaptureSource.gallery`**, which is honest and is also the only value that could be right: a
    /// screenshot is picked, never scanned. Nothing downstream reads it — `POST
    /// /v1/support/screenshots` declares no provenance part — and it is stamped anyway because
    /// ``CapturedImage`` carries provenance *with* the bytes by construction (AL-43).
    private func load(_ item: PhotosPickerItem?) async {
        guard let item, let data = try? await item.loadTransferable(type: Data.self) else { return }
        model.onScreenshotPicked(
            CapturedImage(
                fileName: Self.screenshotFileName,
                data: data,
                capturedVia: CaptureSource.gallery
            )
        )
    }

    /// What the attachment is called in `docs.uploads`. Not user-facing.
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
                    Text(key: "support_loading")
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.background)
        .presentationDetents([.medium, .large])
    }

    private func thread(of ticket: TicketDetail) -> some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            HStack(spacing: MageRideSpacing.xs) {
                Text(SupportLabels.category(ticket.category))
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                Spacer(minLength: MageRideSpacing.xs)
                StatusPill(
                    label: SupportLabels.statusKey(ticket.status).localised,
                    tone: SupportLabels.tone(ticket.status)
                )
            }

            // `IosTicketKt.ticketDescription`, not `ticket.description`: that name is `NSObject`'s on
            // this bridge and the Kotlin one is mangled around it — see the helper's own note.
            Text(IosTicketKt.ticketDescription(ticket: ticket))
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)

            // Oldest first, as the contract sends it: a thread read bottom-up is a thread nobody
            // reads. `assigned` entries are skipped — who is handling a complaint is not the
            // driver's to see, which is the contract's own rule rather than this screen's.
            //
            // Keyed on the position, because a `TicketEvent` carries no id and two `responded`
            // entries a minute apart are genuinely two rows. Indices rather than
            // `Array(_:enumerated())`: **Swift has no key paths into tuples** (the C087 finding).
            ForEach(ticket.thread.indices, id: \.self) { index in
                threadEntry(ticket.thread[index])
            }
        }
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

/// One FAQ article's body (US-16.1). Server-rendered in the driver's own language (D-26).
@MainActor
private struct ArticleSheet: View {

    let article: FaqArticle?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
                Text(article?.title ?? "support_loading".localised)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)

                if let body = article?.body {
                    // Markdown as written. The app ships no renderer and support-svc's articles are
                    // short prose; a half-implemented one that swallowed `#` would be worse than
                    // none. `Text(_:)`'s own AttributedString markdown handles neither headings nor
                    // lists, so it is deliberately not reached for either.
                    Text(body)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.background)
        .presentationDetents([.medium, .large])
    }
}
