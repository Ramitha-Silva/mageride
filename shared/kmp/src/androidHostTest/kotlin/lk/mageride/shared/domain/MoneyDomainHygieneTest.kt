package lk.mageride.shared.domain

import java.io.File
import kotlin.test.Test
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * The four C016 fences that cannot be proved at runtime, checked against the checked-in source.
 *
 * 1. **"No bank-transfer top-up path exists anywhere in the module" (AL-05)** — the component's
 *    fourth definition-of-done item. `TopUpTest` proves the enum has three entries; what a runtime
 *    test cannot prove is that no *other* file grew a bank-transfer route, a `bankTransfer`
 *    parameter or a `topup/bank` URL.
 * 2. **"Mode C tiers show price only before a driver is matched" (AL-19)** — enforced by
 *    `ModeCTier` having no ETA and no distance property. Common code has no reflection, so the
 *    absence of a property is a statement about the source.
 * 3. **"Any commission on a transfer is a bug" (AL-01)** — the transfer rules must not multiply by
 *    a percentage or a basis-point figure at all.
 * 4. **Mode B money is a pass-through** (AL-24, §18b) — `domain/subscription` must never build a
 *    `LedgerEntry`, because MageRide holds none of that money.
 *
 * Like `ModelSourceHygieneTest` (C012) and `PlatformSecurityHygieneTest` (C014) this lives in
 * `androidHostTest`: it is the only source set with a filesystem, and the property is about the
 * checked-in Kotlin rather than anything observable at runtime. **Comments are stripped first** —
 * these files' whole job is to *document* why bank transfer is absent, and a check that fired on
 * the explanation would push the explanation out of the code.
 */
class MoneyDomainHygieneTest {

    @Test
    fun no_money_package_carries_a_bank_transfer_top_up_path() {
        val offenders = moneySources()
            .flatMap { file ->
                val code = codeOf(file.readText())
                BANK_TRANSFER_TOPUP.filter { it in code }.map { "${file.name}: $it" }
            }
            .toList()

        assertTrue(
            offenders.isEmpty(),
            "AL-05 — top-up is OnePay card / OnePay wallet / LankaQR only; found $offenders",
        )
    }

    @Test
    fun the_mode_b_online_transfer_method_is_not_mistaken_for_one() {
        // The counter-check on the rule above: `online_transfer` is a passenger paying a FLEET
        // OWNER directly, pass-through money that never reaches a MageRide wallet. It must still be
        // there, or the test above would be passing because the Mode B payment methods had been
        // quietly deleted.
        val code = codeOf(sourceFile(MODE_B_PAYMENT))

        assertTrue("ONLINE_TRANSFER" in code, "BR-23.10's online-transfer method must survive AL-05")
        assertTrue("requiresSlip" in code)
    }

    @Test
    fun a_mode_c_tier_has_no_arrival_time_and_no_distance() {
        // AL-19: "minutes away" and "distance to driver" are suppressed before a driver is matched.
        val declaration = codeOf(sourceFile(MODE_C_TIERS))
            .substringAfter("public data class ModeCTier(")
            .substringBefore(")")

        ARRIVAL_PROPERTIES.forEach {
            assertTrue(it !in declaration, "AL-19 — a pre-match tier must not carry $it")
        }
        assertTrue("priceMinor" in declaration, "and it must carry the price")
    }

    @Test
    fun the_credit_transfer_rules_take_no_percentage_of_anything() {
        // AL-01: the sender is debited the exact amount and the recipient credited the same exact
        // amount. A commission would have to be computed somewhere, and this is the file it would
        // be computed in.
        val code = codeOf(sourceFile(CREDIT_TRANSFER))

        COMMISSION_ARITHMETIC.forEach {
            assertTrue(it !in code, "AL-01 — a transfer moves the exact value; found $it")
        }
    }

    @Test
    fun mode_b_subscription_money_never_reaches_the_platform_ledger() {
        // §18b: `subscription.payments` must never post to `billing.journal_entries`. C005
        // deliberately gave the table no column tempting anyone to; this keeps the client honest
        // about the same boundary.
        val offenders = sourcesUnder("domain/subscription")
            .filter { "LedgerEntry" in codeOf(it.readText()) }
            .map { it.name }
            .toList()

        assertTrue(offenders.isEmpty(), "AL-24 — Mode B money is a pass-through to the owner; found $offenders")
    }

    // ------------------------------------------------------------------------------------------

    private fun moneySources(): Sequence<File> = MONEY_PACKAGES.asSequence().flatMap { sourcesUnder(it) }

    private fun sourcesUnder(relativePackage: String): Sequence<File> {
        val dir = File(moduleDir(), "src/commonMain/kotlin/lk/mageride/shared/$relativePackage")
        if (!dir.isDirectory) fail("could not read $relativePackage")
        return dir.walkTopDown().filter { it.isFile && it.extension == "kt" }
    }

    private fun sourceFile(relative: String): String {
        val file = File(moduleDir(), relative)
        if (!file.isFile) fail("could not read $relative")
        return file.readText()
    }

    /**
     * The file with its comments blanked out.
     *
     * Same hand-rolled scanner as `PlatformSecurityHygieneTest` (C014), for the same reason: the
     * obvious block-comment regex blows the stack on a file this size. Deliberately naive about
     * string literals — safe for exactly these files, none of which contains a comment opener
     * inside one.
     */
    private fun codeOf(source: String): String {
        val code = StringBuilder(source.length)
        var index = 0
        var depth = 0
        while (index < source.length) {
            val rest = source.length - index
            when {
                rest >= 2 && source.startsWith("/*", index) -> {
                    depth++
                    index += 2
                }

                depth > 0 && rest >= 2 && source.startsWith("*/", index) -> {
                    depth--
                    index += 2
                }

                depth > 0 -> index++

                rest >= 2 && source.startsWith("//", index) -> {
                    while (index < source.length && source[index] != '\n') index++
                }

                else -> code.append(source[index++])
            }
        }
        return code.toString()
    }

    /** `shared/kmp`, whether the test runs from Gradle (module) or from an IDE (repository root). */
    private fun moduleDir(): File {
        val workingDir = requireNotNull(System.getProperty("user.dir")) { "user.dir is unset" }
        var dir: File? = File(workingDir).absoluteFile
        while (dir != null) {
            if (File(dir, MARKER).isFile) return dir
            val nested = File(dir, "shared/kmp")
            if (File(nested, MARKER).isFile) return nested
            dir = dir.parentFile
        }
        fail("could not locate the shared module from $workingDir")
    }

    private companion object {
        const val MARKER = "src/commonMain/kotlin/lk/mageride/shared/domain/wallet/TopUp.kt"
        const val MODE_C_TIERS = "src/commonMain/kotlin/lk/mageride/shared/domain/fare/ModeCTiers.kt"
        const val CREDIT_TRANSFER = "src/commonMain/kotlin/lk/mageride/shared/domain/wallet/CreditTransfer.kt"
        const val MODE_B_PAYMENT = "src/commonMain/kotlin/lk/mageride/shared/domain/subscription/ModeBPayment.kt"

        /** Every package C016 owns. */
        val MONEY_PACKAGES = listOf("domain/fare", "domain/wallet", "domain/subscription")

        /**
         * Spellings a bank-transfer **top-up** would have to take.
         *
         * Deliberately not the bare words "bank" or "transfer": a fleet payout profile names a bank
         * and a driver-to-driver credit movement is a transfer, and both are legitimate.
         */
        val BANK_TRANSFER_TOPUP = listOf(
            "bankTransfer",
            "BANK_TRANSFER",
            "bank_transfer",
            "topup/bank",
            "BankTransfer",
        )

        /** Fields that would put an arrival time on a pre-match tier. */
        val ARRIVAL_PROPERTIES = listOf("eta", "Eta", "ETA", "distance", "Distance", "minutesAway")

        /** Any way a per-transfer commission could be computed. */
        val COMMISSION_ARITHMETIC = listOf(
            "percentOfMinor",
            "basisPointsOfMinor",
            "fractionOfMinor",
            "commissionMinor",
        )
    }
}
