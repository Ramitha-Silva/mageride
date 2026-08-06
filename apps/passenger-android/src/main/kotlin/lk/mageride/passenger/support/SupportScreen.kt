package lk.mageride.passenger.support

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.passenger.R
import lk.mageride.passenger.settings.SettingsTopBar
import lk.mageride.passenger.ui.component.InlineError
import lk.mageride.passenger.ui.component.MageRideCtaTonal
import lk.mageride.passenger.ui.component.SectionLabel
import lk.mageride.passenger.ui.theme.ControlTokens
import lk.mageride.passenger.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketStatus

/**
 * **SCR-PA-030 · support** (US-16.1, US-16.2).
 *
 * The wireframe, top to bottom: a `‹ Support` app bar, *"🔍 Search help"*, an **FAQ** accordion whose
 * rows carry a `＋`, **Your tickets** with a status chip per card, and the tonal *"Raise a ticket"*
 * CTA that opens SCR-PA-030a.
 *
 * **This is a bottom-nav tab that draws a back arrow, and both are the wireframe's.** `‹` is what
 * SCR-PA-030 draws, and the four ways in are the Support tab, SCR-PA-033's drawer row, SCR-PA-017's
 * *"Get help"* and SCR-PA-023's *"Report an issue"* — three of which are somewhere to go back to.
 *
 * @param onBack Pops. On the tab that returns to whichever tab was showing before.
 */
@Composable
internal fun SupportScreen(onBack: () -> Unit, model: SupportViewModel, modifier: Modifier = Modifier) {
    val state by model.state.collectAsStateWithLifecycle()

    Column(modifier = modifier.fillMaxSize()) {
        // C083's bar, not M3's `TopAppBar` — the wireframe draws a 16 px title beside a back chevron
        // with no elevation, and this app has one of those in `settings/SettingsRows.kt`.
        SettingsTopBar(title = stringResource(R.string.support_title), onBack = onBack)

        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = MageRideTheme.spacing.md)
                .padding(bottom = MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            state.error?.let { message ->
                InlineError(message = stringResource(message))
            }
            state.raisedTicketId?.let {
                Text(
                    text = stringResource(R.string.support_ticket_raised),
                    style = MaterialTheme.typography.labelMedium,
                    color = MageRideTheme.status.success,
                )
            }

            SearchField(value = state.search, onValueChange = model::onSearchChange)

            SectionLabel(text = stringResource(R.string.support_faq))
            FaqAccordion(state = state, onToggle = model::toggleArticle)

            SectionLabel(text = stringResource(R.string.support_your_tickets))
            TicketList(state = state, onOpen = model::openTicket)

            MageRideCtaTonal(
                label = stringResource(R.string.support_raise_ticket),
                onClick = model::openTicketSheet,
            )
        }
    }

    SupportSheets(state = state, model = model)
}

/** The wireframe's `.searchbar`. Filters what has been read — see [SupportState.visibleArticles]. */
@Composable
private fun SearchField(value: String, onValueChange: (String) -> Unit, modifier: Modifier = Modifier) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier
            .fillMaxWidth()
            .height(ControlTokens.SearchBar + MageRideTheme.spacing.xs),
        placeholder = { Text(text = stringResource(R.string.support_search_hint)) },
        leadingIcon = { Icon(imageVector = Icons.Filled.Search, contentDescription = null) },
        singleLine = true,
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        textStyle = MaterialTheme.typography.bodyMedium,
    )
}

/**
 * US-16.1's accordion.
 *
 * Every article is listed, not only the matches: the wireframe draws two rows in its resting state
 * with no search typed, which is the FAQ *browse* the search box narrows. A title is server-rendered
 * in the language the app asked for (D-26) — see [SupportRepository].
 */
@Composable
private fun FaqAccordion(state: SupportState, onToggle: (String) -> Unit, modifier: Modifier = Modifier) {
    val articles = state.visibleArticles

    Column(modifier = modifier.fillMaxWidth()) {
        when {
            state.loading && articles.isEmpty() -> Text(
                text = stringResource(R.string.support_loading),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            articles.isEmpty() -> Text(
                text = stringResource(
                    if (state.search.isBlank()) R.string.support_faq_empty else R.string.support_search_empty,
                ),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            else -> articles.forEach { article ->
                FaqRow(
                    title = article.title,
                    expanded = state.expandedArticleId == article.articleId,
                    body = state.expandedArticle?.body,
                    onToggle = { onToggle(article.articleId) },
                )
            }
        }
    }
}

/** One `.listrow` with the wireframe's `＋` / `−`, and the body it reveals. */
@Composable
private fun FaqRow(
    title: String,
    expanded: Boolean,
    body: String?,
    onToggle: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier = modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable(onClick = onToggle)
                .padding(vertical = MageRideTheme.spacing.xs),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Text(
                text = title,
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
            Text(
                text = if (expanded) FaqLabels.COLLAPSE else FaqLabels.EXPAND,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        if (!expanded) return@Column

        Text(
            // Markdown as written. The app ships no renderer and support-svc's articles are short
            // prose; a half-implemented one that swallowed a `#` would be worse than none.
            text = body ?: stringResource(R.string.support_loading),
            modifier = Modifier.padding(bottom = MageRideTheme.spacing.xs),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** *"Your tickets"* — one card per row, with its status chip (US-16.2). */
@Composable
private fun TicketList(state: SupportState, onOpen: (String) -> Unit, modifier: Modifier = Modifier) {
    Column(modifier = modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
        when {
            state.loading -> Unit

            state.tickets.isEmpty() -> Text(
                text = stringResource(R.string.support_tickets_empty),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            else -> state.tickets.forEach { ticket ->
                TicketRow(ticket = ticket, onClick = { onOpen(ticket.ticketId) })
            }
        }
    }
}

/**
 * One ticket card.
 *
 * The wireframe prints `#TK-4521 · Wrong fare` and an *"Attached trip PAX-90431-0617"* line. **This
 * platform mints neither identifier**: a ticket id is a ULID and there is no `TK-` series, and no
 * contract carries a human-readable passenger or trip number (the same finding C083 recorded about
 * `PAX-90431`). So the card leads with the **category**, which is what a passenger recognises, and
 * the attached-trip line is drawn only when there is one — without the invented number.
 */
@Composable
private fun TicketRow(ticket: Ticket, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Surface(
        modifier = modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        color = MaterialTheme.colorScheme.surface,
    ) {
        Column(modifier = Modifier.padding(MageRideTheme.spacing.sm)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
            ) {
                Text(
                    text = categoryLabel(ticket.category),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                StatusPill(status = ticket.status)
            }
            if (ticket.tripId != null) {
                Text(
                    text = stringResource(R.string.support_attached_trip),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/**
 * The wireframe's status chip — `Open` amber, `In progress` blue, `Resolved` green.
 *
 * One table for the card and the thread header alike: the same ticket wearing two colours on two
 * surfaces is the kind of thing a passenger reads as two different tickets.
 */
@Composable
internal fun StatusPill(status: TicketStatus, modifier: Modifier = Modifier) {
    val tint = when (status) {
        TicketStatus.OPEN -> MageRideTheme.status.warning
        TicketStatus.IN_PROGRESS -> MaterialTheme.colorScheme.secondary
        TicketStatus.RESOLVED -> MageRideTheme.status.success
    }

    Text(
        text = stringResource(SupportLabels.status(status)),
        modifier = modifier
            .background(tint.copy(alpha = PILL_TINT), RoundedCornerShape(MageRideTheme.radius.sm))
            .padding(horizontal = MageRideTheme.spacing.xs, vertical = MageRideTheme.spacing.xxs),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurface,
    )
}

/**
 * The accordion's `＋` and `−`.
 *
 * Glyphs the wireframe prints, not sentences: they are the same two characters in Sinhala, Tamil and
 * English, and three identical values in the three `strings.xml` files is what `StringResourceTest`
 * reads as a key nobody translated. The row's title beside them is server-rendered copy.
 */
private object FaqLabels {
    const val EXPAND: String = "＋"
    const val COLLAPSE: String = "−"
}

/** The tint strength every status pill in this app is drawn at (C082's `StatusPill`). */
private const val PILL_TINT = 0.16f
