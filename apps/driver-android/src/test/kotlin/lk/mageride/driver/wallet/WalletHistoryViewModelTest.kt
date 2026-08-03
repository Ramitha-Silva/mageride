package lk.mageride.driver.wallet

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.wallet.WalletTransaction
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.util.BusinessCalendar
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-025 — the two filters, and the download that is also the receipt.
 *
 * The chips are the device's and the date range is the server's; [WalletHistoryViewModel]'s KDoc
 * says why, and this is where the difference is asserted.
 */
class WalletHistoryViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val exporter = FakeStatementExporter()

    private val ledger = listOf(
        ledgerLine("01JENTRY0000000000000001", LedgerKinds.DAILY_FEE, amountMinor = -10_000L),
        ledgerLine("01JENTRY0000000000000002", LedgerKinds.TOPUP, amountMinor = 200_000L),
        ledgerLine("01JENTRY0000000000000003", LedgerKinds.VOUCHER_PURCHASE, amountMinor = ONE_THOUSAND),
        ledgerLine("01JENTRY0000000000000004", LedgerKinds.DRIVER_TRANSFER, amountMinor = -50_000L),
        ledgerLine("01JENTRY0000000000000005", LedgerKinds.TRIP_PAYMENT, amountMinor = 48_000L),
    )

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("listWalletTransactions", onePage(*ledger.toTypedArray()))
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_chips_filter_the_page_already_read_and_re_hit_nothing() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        val readsAfterLoad = backend.callsTo("listWalletTransactions").size

        model.selectFilter(HistoryFilter.FEES)
        assertEquals(listOf(LedgerKinds.DAILY_FEE), model.state.value.visible.map(WalletTransaction::kind))

        // A bulk voucher is a top-up from the ledger's side: US-9.19 credits the buyer's own wallet
        // at purchase, and it cost less than it credited.
        model.selectFilter(HistoryFilter.TOPUPS)
        assertEquals(
            listOf(LedgerKinds.TOPUP, LedgerKinds.VOUCHER_PURCHASE),
            model.state.value.visible.map(WalletTransaction::kind),
        )

        model.selectFilter(HistoryFilter.TRANSFERS)
        assertEquals(listOf(LedgerKinds.DRIVER_TRANSFER), model.state.value.visible.map(WalletTransaction::kind))

        model.selectFilter(HistoryFilter.ALL)
        assertEquals(ledger.size, model.state.value.visible.size)

        assertEquals(readsAfterLoad, backend.callsTo("listWalletTransactions").size, "no chip re-reads the ledger")
    }

    @Test
    fun the_date_range_is_the_servers_and_reaches_it_as_colombo_business_dates() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        val from = BusinessCalendar.businessDate(lk.mageride.shared.testing.fixture.Fixtures.NOW)
        val to = BusinessCalendar.plusDays(from, 7)
        model.setRange(from, to)
        model.state.await { !it.loading }

        val call = backend.lastCall("listWalletTransactions")
        assertEquals(from.toString(), call.query["from"])
        assertEquals(to.toString(), call.query["to"])
    }

    @Test
    fun an_unknown_ledger_kind_renders_as_a_wallet_entry_rather_than_breaking_the_list() {
        // `WalletTransaction.kind` is a machine key, not an enum: the CHECK constraint behind it
        // grew twice already (1108 added `fleet_invoice`, 1109 `driver_payout`).
        assertEquals(lk.mageride.driver.R.string.wallet_kind_other, LedgerKinds.labelFor("something_new"))
        assertEquals(lk.mageride.driver.R.string.wallet_kind_daily_fee, LedgerKinds.labelFor(LedgerKinds.DAILY_FEE))
    }

    @Test
    fun the_download_asks_for_the_media_type_it_is_named_after() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.export(StatementFormat.CSV)
        model.state.await { it.exported != null }

        assertEquals(StatementFormat.CSV, exporter.lastFormat)
        assertNotNull(exporter.lastBytes)
        assertTrue(
            backend.lastCall("listWalletTransactions").headers["Accept"].orEmpty().startsWith("text/csv"),
            "the requested type comes first; content negotiation appends the JSON pair after it",
        )
    }

    @Test
    fun a_statement_is_named_for_the_range_it_covers_and_not_for_the_chip() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        // A statement is evidence of what the ledger did. One that quietly omitted the rows a
        // driver had filtered out would not reconcile with the balance printed on it.
        model.selectFilter(HistoryFilter.FEES)
        val from = BusinessCalendar.businessDate(lk.mageride.shared.testing.fixture.Fixtures.NOW)
        model.setRange(from, from)
        model.state.await { !it.loading }

        model.export(StatementFormat.PDF)
        model.state.await { it.exported != null }

        assertEquals("mageride-wallet-$from-$from.pdf", exporter.lastFileName)
        assertNull(backend.lastCall("listWalletTransactions").query["kind"], "there is no such parameter")
    }

    @Test
    fun a_download_nothing_can_receive_is_reported_rather_than_claimed() = runBlocking {
        exporter.handled = false

        val model = viewModel()
        model.state.await { !it.loading }
        model.export(StatementFormat.CSV)

        val state = model.state.await { it.error != null }
        assertNull(state.exported)
        assertNull(state.exporting)
    }

    private suspend fun viewModel(): WalletHistoryViewModel {
        val api = backend.mageRideApi()
        return main.own(
            WalletHistoryViewModel(
                identity = identity(backend, signedInSessions(backend)),
                wallet = WalletRepository(wallet = api.wallet, subscription = api.subscription),
                exporter = exporter,
            ),
        )
    }
}
