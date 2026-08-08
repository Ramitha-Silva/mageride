import MageRideShared
import SwiftUI

/// **SCR-PI-030 · support** (US-16.1, US-16.2).
///
/// The cell, top to bottom: a **large title** *"Support"*, *"🔍 Search help"*, an **FAQ** group whose
/// rows carry a `＋`, **Your tickets** with a status chip per card, and the tonal *"Raise a ticket"*
/// CTA that opens SCR-PI-030a.
///
/// **This is a tab root, not a pushed screen, and that is why it draws no back chevron.**
/// `passenger_android.html`'s SCR-PA-030 draws a `‹ Support` app bar because that app reaches it
/// from a drawer; here it is **tab 3** (``PassengerTab/support``) and the cell draws a `largetitle`.
/// The Menu tab's *Help & support* row and SCR-PI-027's own row both open the same destination,
/// which switches to the tab rather than pushing a second copy — see ``PassengerNavigator/open(_:)``.
///
/// **The FAQ list is drawn whether or not anything is typed**, which is the cell's own resting
/// state: it draws two rows with an empty search field, so the search *narrows* a browse rather than
/// revealing one. `apps/driver-ios`'s SCR-DI-033 draws the opposite and its screen behaves the
/// opposite way; both follow their own wireframe.
@MainActor
struct SupportScreen: View {

    @StateObject private var model: SupportModel

    init(support: SupportRepository, sessions: PassengerSessions, preferences: AppPreferences) {
        _model = StateObject(
            wrappedValue: SupportModel(support: support, sessions: sessions, preferences: preferences)
        )
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                if let errorKey = model.state.errorKey {
                    InfoBanner(messageKey: errorKey, tone: .error, symbolName: "exclamationmark.triangle.fill")
                        .onTapGesture(perform: model.dismissError)
                }
                if model.state.raisedTicketId != nil {
                    InfoBanner(
                        messageKey: "support_ticket_raised",
                        tone: .ok,
                        symbolName: "checkmark.circle.fill"
                    )
                }

                SearchField(
                    placeholderKey: "support_search_hint",
                    value: Binding(get: { model.state.search }, set: model.onSearchChange)
                )

                SectionLabel(key: "support_faq")
                faqAccordion

                SectionLabel(key: "support_your_tickets")
                ticketList

                Button(action: model.openTicketSheet) {
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
        .refreshable { await model.refresh() }
        .task { await model.refresh() }
        // One binding for two overlays. SwiftUI presents one sheet per context and silently drops
        // the rest, which is the trap C100 recorded on SCR-PI-025a.
        .sheet(item: Binding(get: { model.state.sheet }, set: { if $0 == nil { model.closeSheet() } })) { sheet in
            SupportSheets(sheet: sheet, model: model)
        }
    }

    /// US-16.1's accordion.
    ///
    /// **Δ iOS — a `DisclosureGroup`, which is the cell's own clause** (*"`List` + `DisclosureGroup`
    /// FAQ"*) where the Android twin draws a `＋` / `−` glyph it toggles itself. The two are the same
    /// affordance in each platform's vocabulary, and the disclosure chevron is the one every other
    /// expanding row on iOS uses — which is why ``SupportLabels/expand`` exists but nothing draws it.
    ///
    /// **One open at a time**, driven from the model rather than from `DisclosureGroup`'s own state:
    /// the body is *fetched*, so which row is open is a fact the model has to know anyway, and two
    /// open bodies would push *"Your tickets"* off a 5.4" screen.
    @ViewBuilder
    private var faqAccordion: some View {
        let articles = model.state.visibleArticles

        if model.state.isLoading, articles.isEmpty {
            Text(key: "support_loading")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        } else if articles.isEmpty {
            Text(key: model.state.search.isEmpty ? "support_faq_empty" : "support_search_empty")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        } else {
            GroupedList {
                // Keyed on the article rather than on an index pair: **Swift has no key paths into
                // tuples** (the C087 finding), so `Array(_:enumerated())` cannot supply a `ForEach`
                // id here. "Is this the last row" is asked of the collection instead.
                ForEach(articles, id: \.articleId) { article in
                    faqRow(article, showsSeparator: article.articleId != articles.last?.articleId)
                }
            }
        }
    }

    private func faqRow(_ article: FaqSummary, showsSeparator: Bool) -> some View {
        VStack(spacing: 0) {
            DisclosureGroup(
                isExpanded: Binding(
                    get: { model.state.expandedArticleId == article.articleId },
                    set: { _ in Task { await model.toggleArticle(articleId: article.articleId) } }
                )
            ) {
                // Markdown as written. The app ships no renderer and support-svc's articles are
                // short prose; a half-implemented one that swallowed a `#` would be worse than
                // none. `Text(_:)`'s own `AttributedString` markdown handles neither headings nor
                // lists, so it is deliberately not reached for either.
                Text(model.state.expandedArticle?.body ?? "support_loading".localised)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.bottom, MageRideSpacing.xs)
            } label: {
                Text(article.title)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .frame(minHeight: MageRideControl.minimumTapTarget)
            }
            .tint(MageRideColor.onSurfaceVariant)
            .padding(.horizontal, MageRideSpacing.sm)

            if showsSeparator {
                Rectangle()
                    .fill(MageRideColor.surfaceVariant)
                    .frame(height: MageRideControl.hairline)
                    .padding(.leading, MageRideSpacing.sm)
            }
        }
    }

    /// *"Your tickets"* — one card per row, with its status chip (US-16.2).
    @ViewBuilder
    private var ticketList: some View {
        if model.state.isLoading {
            LoadingRow(messageKey: "support_loading")
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

    /// One ticket card.
    ///
    /// The wireframe prints `#TK-4521 · Wrong fare`. **This platform mints no such identifier**: a
    /// `support.tickets` id is a ULID, there is no `TK-` series, and no contract carries a
    /// human-readable ticket number — the fourth time this has come up (C074's `DRV-22011`, C083's
    /// and C101's `PAX-90431`, C084's `#TK-4521`). So the row leads with the **category**, which is
    /// what a passenger recognises, and the id is not drawn at all: a twenty-six-character
    /// identifier in a list row is noise nobody reads out.
    private func ticketRow(_ ticket: Ticket, showsSeparator: Bool) -> some View {
        VStack(spacing: 0) {
            Button {
                Task { await model.openTicket(ticketId: ticket.ticketId) }
            } label: {
                HStack(spacing: MageRideSpacing.xs) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(SupportLabels.category(ticket.category))
                            .mageFont(.body)
                            .foregroundStyle(MageRideColor.onSurface)
                            .multilineTextAlignment(.leading)

                        // The cell's *"Attached trip PAX-90431-0617"* line, without the invented
                        // number: drawn only when the ticket has a trip on it at all.
                        if ticket.tripId != nil {
                            Text(key: "support_attached_trip")
                                .mageFont(.caption)
                                .foregroundStyle(MageRideColor.onSurfaceVariant)
                        }
                    }

                    Spacer(minLength: MageRideSpacing.xs)
                    StatusPill(titleKey: SupportLabels.statusKey(ticket.status), tone: SupportLabels.tone(ticket.status))
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
