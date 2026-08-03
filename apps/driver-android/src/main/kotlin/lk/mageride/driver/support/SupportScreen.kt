package lk.mageride.driver.support

import androidx.compose.foundation.BorderStroke
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
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.DashboardBanner
import lk.mageride.driver.ui.component.MageRideCtaTonal
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.component.StatusPill
import lk.mageride.driver.ui.theme.ControlTokens
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.support.FaqSummary
import lk.mageride.shared.data.models.support.Ticket
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-033 · support and the daily-fee refund** (US-16.1/16.2, US-9.23).
 *
 * The wireframe, top to bottom: a `‹ Support` app bar, *"🔍 Search help"*, **Quick actions** with the
 * primary-outlined *"Request daily-fee refund … Request"* card, **Your tickets** with a status chip
 * per row, and the tonal *"Raise a ticket"* CTA.
 *
 * **Both entry points open the same sheet** (SCR-DA-033a) and both post `POST /v1/support/tickets`;
 * only `category` differs, and it is what routes the row to Finance or Support. See
 * [SupportViewModel].
 *
 * The FAQ list under the search box is US-16.1's half of the screen. The wireframe draws the search
 * field and no results, because it draws the resting state — typing is what reveals them.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun SupportScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: SupportViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.support_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.action_back),
                        )
                    }
                },
            )
        },
    ) { insets ->
        Column(
            modifier = Modifier
                .padding(insets)
                .fillMaxSize()
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            state.error?.let { message ->
                DashboardBanner(text = stringResource(message), accent = MaterialTheme.colorScheme.error)
            }
            state.raisedTicketId?.let {
                DashboardBanner(
                    text = stringResource(R.string.support_ticket_raised),
                    accent = MageRideTheme.status.success,
                )
            }

            Column(
                modifier = Modifier.padding(MageRideTheme.spacing.md),
                verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
            ) {
                SearchField(value = state.search, onValueChange = viewModel::onSearchChange)

                FaqResults(
                    articles = state.visibleArticles,
                    searching = state.search.isNotBlank(),
                    onOpen = viewModel::openArticle,
                )

                SectionLabel(text = stringResource(R.string.support_quick_actions))
                RefundCard(onRequest = { viewModel.openTicketSheet(SupportCategories.DAILY_FEE_REFUND) })

                SectionLabel(text = stringResource(R.string.support_your_tickets))
                TicketList(state = state, onOpen = viewModel::openTicket)

                MageRideCtaTonal(
                    label = stringResource(R.string.support_raise_ticket),
                    onClick = { viewModel.openTicketSheet(SupportCategories.GENERAL) },
                )
            }
        }
    }

    SupportSheets(state = state, viewModel = viewModel)
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
        leadingIcon = { Icon(imageVector = Icons.Outlined.Search, contentDescription = null) },
        singleLine = true,
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        textStyle = MaterialTheme.typography.bodyMedium,
    )
}

/**
 * US-16.1's articles.
 *
 * Drawn only while something is typed: the wireframe's resting state is the search field with the
 * quick actions under it, and a full FAQ index pushed above *"Request daily-fee refund"* would bury
 * the one action a driver opens this screen for.
 */
@Composable
private fun FaqResults(
    articles: List<FaqSummary>,
    searching: Boolean,
    onOpen: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    if (!searching) return

    Column(modifier = modifier.fillMaxWidth()) {
        if (articles.isEmpty()) {
            Text(
                text = stringResource(R.string.support_search_empty),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            return@Column
        }
        articles.forEach { article ->
            Text(
                text = article.title,
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { onOpen(article.articleId) }
                    .padding(vertical = MageRideTheme.spacing.xs),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
            )
        }
    }
}

/**
 * *"Request daily-fee refund · e.g. app crashed on Go Online"* (US-9.23, US-14.11).
 *
 * The wireframe outlines this card in `primary` — it is the one action on the screen that moves
 * money. **Request** opens SCR-DA-033a with the category fixed, because a refund is a ticket with a
 * category and not an endpoint of its own; the reversal itself is an admin decision (US-14.11).
 */
@Composable
private fun RefundCard(onRequest: () -> Unit, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.md),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = BorderStroke(ControlTokens.Border, MaterialTheme.colorScheme.primary),
    ) {
        Row(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = stringResource(R.string.support_refund_title),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurface,
                )
                Text(
                    text = stringResource(R.string.support_refund_hint),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            TextButton(onClick = onRequest) {
                Text(text = stringResource(R.string.support_refund_action))
            }
        }
    }
}

/** *"Your tickets"* — `#TK-9012 · Fee charged in error` with its status chip. */
@Composable
private fun TicketList(state: SupportState, onOpen: (String) -> Unit, modifier: Modifier = Modifier) {
    Column(modifier = modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xxs)) {
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
 * One ticket row.
 *
 * The wireframe prints `#TK-9012`; a ticket id on this platform is a ULID and there is no `TK-`
 * series to render — the same finding C074 recorded about `DRV-22011`. The row therefore leads with
 * the **category**, which is what the driver recognises, and the id is not drawn at all: a
 * twenty-six-character identifier in a list row is noise nobody reads out.
 */
@Composable
private fun TicketRow(ticket: Ticket, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = MageRideTheme.spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
    ) {
        Text(
            text = categoryLabel(ticket.category),
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
        StatusPill(
            label = stringResource(SupportLabels.status(ticket.status)),
            tone = SupportLabels.tone(ticket.status),
        )
    }
}
