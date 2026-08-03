package lk.mageride.driver.wallet

import lk.mageride.driver.nav.DriverRoute
import lk.mageride.driver.nav.isTabRoute
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.domain.wallet.CreditTransferRules
import lk.mageride.shared.domain.wallet.TopupMethod
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The fences C073's prompt draws, enforced rather than remembered.
 *
 * > *"Top-up methods are card (OnePay), OnePay wallet and LankaQR deep link only — no bank
 * > transfer. Credit requests are Driver-ID only; the QR-scan tile was removed (AL-34). Transfers
 * > move the exact value. Never render a commission line."*
 *
 * Some of that is checkable as a fact about types; the rest — *"no bank-transfer option appears
 * anywhere"* — is only checkable as a fact about the **source and the copy**, which is what
 * `MoneyDomainHygieneTest` does for `:shared`'s half of the same rule. A screen is where a removed
 * payment method comes back, because a screen is where somebody adds a tile.
 *
 * **Comments are stripped before the source is searched**, for the reason that test gives: half of
 * this package's job is to *document* why bank transfer is absent, and a check that fired on the
 * explanation would push the explanation out of the code.
 */
class WalletFenceTest {

    private val sources = File("src/main/kotlin/lk/mageride/driver/wallet")

    private val strings = listOf("values", "values-si", "values-ta")
        .map { File("src/main/res/$it/strings.xml") }

    @Test
    fun there_are_exactly_three_top_up_methods_and_none_of_them_is_a_bank_transfer() {
        // AL-05 removed the route rather than deprecating it: `wallet.yaml` carries no
        // `POST /v1/wallet/topup/bank-transfer`, `WalletApi` has no such call, and `TopupMethod`
        // has no fourth entry a tile could select.
        assertEquals(
            listOf(TopupMethod.ONEPAY_CARD, TopupMethod.ONEPAY_WALLET, TopupMethod.LANKAQR),
            TopupMethod.entries.toList(),
        )
    }

    @Test
    fun no_source_file_in_this_package_carries_a_bank_transfer_top_up_path() {
        val offenders = kotlinSources().flatMap { file ->
            val code = codeOf(file.readText())
            BANK_TRANSFER_TOPUP.filter { it in code }.map { "${file.name}: $it" }
        }

        assertTrue(offenders.isEmpty(), "AL-05 — top-up is OnePay card / OnePay wallet / LankaQR only: $offenders")
    }

    @Test
    fun no_wallet_copy_offers_a_bank_transfer_in_any_of_the_three_languages() {
        // The Sinhala and Tamil files are where a removed method is likeliest to survive a review:
        // nobody reading the English screen would see it.
        walletCopy().forEach { (path, values) ->
            assertTrue(values.isNotEmpty(), "no wallet copy in $path")
            assertTrue(
                values.none { value -> BANK_TRANSFER_COPY.any { value.contains(it, ignoreCase = true) } },
                "$path offers a bank transfer",
            )
        }
    }

    @Test
    fun nothing_in_this_package_scans_a_code_to_find_a_driver() {
        // AL-34 removed SCR-DA-023's QR-scan path. `DocumentCaptureCoordinator` is C069's scanner
        // seam and the only camera door in the app; a wallet screen reaching for it would be that
        // path coming back. ZXing is here as an ENCODER for AL-15's fallback and never as a reader,
        // which is why `MultiFormatReader` is on the list and `QRCodeWriter` is not.
        val offenders = kotlinSources().flatMap { file ->
            val code = codeOf(file.readText())
            SCANNER_SEAMS.filter { it in code }.map { "${file.name}: $it" }
        }

        assertTrue(offenders.isEmpty(), "credit requests are Driver-ID only (AL-34): $offenders")
    }

    @Test
    fun a_transfer_carries_no_fee_leg_at_any_amount() {
        // Not a configured rate of zero — there is no journal kind a commission could post under,
        // and `entryFor` produces two postings that sum to zero (AL-01, D-09).
        assertEquals(0, CreditTransferRules.COMMISSION_BPS)
        listOf(1L, 50_000L, ONE_THOUSAND, 1_000_000L).forEach { minor ->
            val amount = Money.ofMinor(minor)
            assertEquals(amount, CreditTransferRules.debitedFromSender(amount))
            assertEquals(amount, CreditTransferRules.creditedToRecipient(amount))
            assertEquals(Money.ZERO, CreditTransferRules.feeFor(amount))
        }
    }

    @Test
    fun no_wallet_copy_names_a_commission() {
        // "Never render a commission line." The wireframe's own history row says "Reseller
        // transfer", which is the trap: AL-01 makes the margin the purchase discount, taken once,
        // and there is nothing to take off a transfer to name.
        walletCopy().forEach { (path, values) ->
            assertTrue(values.none { it.contains("commission", ignoreCase = true) }, "$path names a commission")
        }
    }

    @Test
    fun the_five_wallet_screens_are_registered_and_only_the_first_is_a_tab() {
        val pushed = listOf(
            DriverRoute.WalletTopUp,
            DriverRoute.WalletRequestCredit,
            DriverRoute.WalletTransfer,
            DriverRoute.WalletHistory,
        )

        (pushed + DriverRoute.Wallet).forEach { assertTrue(it in DriverRoute.Static, "${it.path} is not registered") }

        assertTrue(isTabRoute(DriverRoute.Wallet.path), "SCR-DA-021 IS bottom-nav tab 3")
        pushed.forEach { assertFalse(isTabRoute(it.path), "${it.path} is pushed, and shows no bottom bar") }
    }

    /** Every `wallet_*` value in each `strings.xml`, keyed by path. */
    private fun walletCopy(): Map<String, List<String>> = strings.associate { file ->
        assertTrue(file.exists(), "missing ${file.path}")
        file.path to WALLET_STRING.findAll(file.readText()).map { it.groupValues[1] }.toList()
    }

    private fun kotlinSources(): List<File> {
        assertTrue(sources.isDirectory, "the wallet package is not where this test looks: ${sources.path}")
        return sources.walkTopDown().filter { it.isFile && it.extension == "kt" }.toList()
    }

    /** [text] with its block and line comments removed. */
    private fun codeOf(text: String): String = text
        .replace(Regex("""/\*.*?\*/""", RegexOption.DOT_MATCHES_ALL), "")
        .replace(Regex("""//.*"""), "")

    private companion object {
        val WALLET_STRING = Regex("""<string name="wallet_[^"]*">(.*?)</string>""", RegexOption.DOT_MATCHES_ALL)

        /**
         * Spellings a bank-transfer **top-up** would take in code.
         *
         * Deliberately not the bare words "bank" or "transfer" — `PaymentHandoff` opens a bank app
         * for LankaQR and a driver-to-driver credit movement is a transfer, and both are legitimate.
         * Same list `MoneyDomainHygieneTest` uses, for the same reason.
         */
        val BANK_TRANSFER_TOPUP = listOf("bankTransfer", "BANK_TRANSFER", "bank_transfer", "topup/bank", "BankTransfer")

        /** The same method as a driver would read it. Copy, not code, so the prose forms matter. */
        val BANK_TRANSFER_COPY = listOf("bank transfer", "bank deposit", "බැංකු මාරු", "வங்கி பரிமாற்ற")

        /** The camera doors this app has. A wallet screen opening one is AL-34's path returning. */
        val SCANNER_SEAMS =
            listOf("DocumentCaptureCoordinator", "DocumentCaptureTarget", "CameraX", "MultiFormatReader")
    }
}
