import MageRideShared
import SwiftUI

/// **SCR-DI-033 · support and the daily-fee refund** (US-16.1/16.2, US-9.23).
///
/// The wireframe, top to bottom: a `Support` large title, *"🔍 Search help"*, **Quick actions** with
/// the primary-outlined *"Request daily-fee refund … Request"* card, **Your tickets** with a status
/// chip per row, and the tonal *"Raise a ticket"* CTA.
///
/// **Both entry points open the same sheet** (SCR-DI-033a) and both post `POST /v1/support/tickets`;
/// only `category` differs, and it is what routes the row to Finance or Support. See
/// ``SupportModel``.
///
/// The FAQ list under the search box is US-16.1's half of the screen. The wireframe draws the search
/// field and no results, because it draws the resting state — typing is what reveals them.
///
/// It hangs off the **Menu** tab (``DriverRoute/tab``), which is what makes the system back button
/// say `‹ Menu` rather than the Android twin's own `‹ Support` app bar.
@MainActor
struct SupportScreen: View {

    @StateObject private var model: SupportModel

    init(model: @autoclosure @escaping () -> SupportModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                if let errorKey = model.state.errorKey {
                    DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                        .onTapGesture(perform: model.dismissError)
                }
                if model.state.raisedTicketId != nil {
                    DashboardBanner(
                        text: "support_ticket_raised".localised,
                        accent: MageRideColor.success,
                        symbolName: "checkmark.circle.fill"
                    )
                }

                SearchField(
                    placeholderKey: "support_search_hint",
                    value: Binding(get: { model.state.search }, set: model.onSearchChange)
                )

                if model.state.isSearching {
                    faqResults
                }

                SectionLabel(key: "support_quick_actions")
                refundCard

                SectionLabel(key: "support_your_tickets")
                ticketList

                Button { model.openTicketSheet(category: SupportCategories.general) } label: {
                    Text(key: "support_raise_ticket")
                }
                .buttonStyle(.mageCtaTonal)
                .padding(.top, MageRideSpacing.xxs)
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "support_title"))
        .navigationBarTitleDisplayMode(.large)
        .task { await model.refresh() }
        // One binding for three overlays. SwiftUI presents one sheet per context and silently drops
        // the rest, which is the trap C091 recorded on SCR-DI-022.
        .sheet(item: Binding(get: { model.state.sheet }, set: { if $0 == nil { model.closeSheet() } })) { sheet in
            SupportSheets(sheet: sheet, model: model)
        }
    }

    /// US-16.1's articles, drawn only while something is typed.
    @ViewBuilder
    private var faqResults: some View {
        if model.state.visibleArticles.isEmpty {
            Text(key: "support_search_empty")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        } else {
            // Keyed on the article rather than on an index pair: **Swift has no key paths into
            // tuples** (the C087 finding), so `Array(_:enumerated())` cannot supply a `ForEach` id
            // here or anywhere else in this cluster. "Is this the last row" is asked of the
            // collection instead.
            let articles = model.state.visibleArticles
            GroupedList {
                ForEach(articles, id: \.articleId) { article in
                    Button {
                        Task { await model.openArticle(articleId: article.articleId) }
                    } label: {
                        HStack(spacing: MageRideSpacing.xs) {
                            Text(article.title)
                                .mageFont(.body)
                                .foregroundStyle(MageRideColor.onSurface)
                                .multilineTextAlignment(.leading)
                            Spacer(minLength: MageRideSpacing.xs)
                            Image(systemName: "chevron.right")
                                .font(.caption)
                                .foregroundStyle(MageRideColor.outlineVariant)
                        }
                        .padding(.horizontal, MageRideSpacing.sm)
                        .frame(minHeight: MageRideControl.minimumTapTarget)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)

                    if article.articleId != articles.last?.articleId {
                        Rectangle()
                            .fill(MageRideColor.surfaceVariant)
                            .frame(height: MageRideControl.hairline)
                            .padding(.leading, MageRideSpacing.sm)
                    }
                }
            }
        }
    }

    /// *"Request daily-fee refund · e.g. app crashed on Go Online"* (US-9.23, US-14.11).
    ///
    /// The wireframe outlines this card in `primary` — it is the one action on the screen that moves
    /// money. **Request** opens SCR-DI-033a with the category fixed, because a refund is a ticket
    /// with a category and not an endpoint of its own; the reversal itself is an admin decision.
    private var refundCard: some View {
        HStack(spacing: MageRideSpacing.xs) {
            VStack(alignment: .leading, spacing: 1) {
                Text(key: "support_refund_title")
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                Text(key: "support_refund_hint")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
            Spacer(minLength: MageRideSpacing.xs)

            Button { model.openTicketSheet(category: SupportCategories.dailyFeeRefund) } label: {
                Text(key: "support_refund_action")
                    .mageFont(.subtitle)
                    .foregroundStyle(MageRideColor.primary)
                    .frame(minHeight: MageRideControl.minimumTapTarget)
            }
            .buttonStyle(.plain)
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity)
        .background(
            MageRideColor.background,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .overlay {
            RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                .strokeBorder(MageRideColor.primary, lineWidth: 2)
        }
    }

    /// *"Your tickets"* — the category with its status chip.
    @ViewBuilder
    private var ticketList: some View {
        if model.state.isLoading {
            ProgressView()
                .frame(maxWidth: .infinity)
        } else if model.state.tickets.isEmpty {
            Text(key: "support_tickets_empty")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        } else {
            GroupedList {
                ForEach(model.state.tickets, id: \.ticketId) { ticket in
                    ticketRow(ticket, showsSeparator: ticket.ticketId != model.state.tickets.last?.ticketId)
                }
            }
        }
    }

    /// One ticket row.
    ///
    /// The wireframe prints `#TK-9012`; a ticket id on this platform is a ULID and there is no `TK-`
    /// series to render — the same finding C092 recorded about `DRV-22011`. The row therefore leads
    /// with the **category**, which is what the driver recognises, and the id is not drawn at all: a
    /// twenty-six-character identifier in a list row is noise nobody reads out.
    private func ticketRow(_ ticket: Ticket, showsSeparator: Bool) -> some View {
        VStack(spacing: 0) {
            Button {
                Task { await model.openTicket(ticketId: ticket.ticketId) }
            } label: {
                HStack(spacing: MageRideSpacing.xs) {
                    Text(SupportLabels.category(ticket.category))
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                        .multilineTextAlignment(.leading)
                    Spacer(minLength: MageRideSpacing.xs)
                    StatusPill(
                        label: SupportLabels.statusKey(ticket.status).localised,
                        tone: SupportLabels.tone(ticket.status)
                    )
                }
                .padding(.horizontal, MageRideSpacing.sm)
                .frame(minHeight: MageRideControl.minimumTapTarget)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityElement(children: .combine)

            if showsSeparator {
                Rectangle()
                    .fill(MageRideColor.surfaceVariant)
                    .frame(height: MageRideControl.hairline)
                    .padding(.leading, MageRideSpacing.sm)
            }
        }
    }
}
