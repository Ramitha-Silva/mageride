package lk.mageride.shared.domain.subscription

import kotlinx.datetime.LocalDate
import lk.mageride.shared.data.models.subscription.PayTo
import lk.mageride.shared.data.models.subscription.SubscriberMonthStatus
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.domain.fare.FarePaymentAction
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue

/**
 * How a Mode B subscriber pays (BR-23.10, AL-24, AL-49).
 *
 * Two of the five methods have no gateway at all: `online_transfer` and `cash` settle on the fleet
 * owner's decision, which is why `pending_verification` is a first-class status.
 */
class ModeBPaymentTest {

    private val verifiedPayTo = PayTo(
        lankaqrImageUrl = "https://cdn.mageride.lk/qr/1.png",
        bank = "Commercial Bank",
        branch = "Kollupitiya",
        accountNo = "1234567890",
        accountHolderName = "Silverline Transport (Pvt) Ltd",
    )

    private fun payment(
        method: SubscriptionPayMethod,
        status: SubscriptionPaymentStatus = SubscriptionPaymentStatus.INITIATED,
        payTo: PayTo? = verifiedPayTo,
        redirectUrl: String? = null,
        qrPayload: String? = null,
    ) = SubscriptionPayment(
        paymentId = "01JSPY0000000000000000001",
        subscriptionId = "01JSUB0000000000000000001",
        method = method,
        amountMinor = 300_000,
        status = status,
        periodMonth = LocalDate(2026, 7, 1),
        payTo = payTo,
        redirectUrl = redirectUrl,
        qrPayload = qrPayload,
    )

    // ----------------------------------------------------------------------------------------
    // The five methods
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_two_human_settled_methods_are_the_ones_the_owner_confirms() {
        assertTrue(ModeBPaymentRules.requiresOwnerConfirmation(SubscriptionPayMethod.ONLINE_TRANSFER))
        assertTrue(ModeBPaymentRules.requiresOwnerConfirmation(SubscriptionPayMethod.CASH))

        listOf(
            SubscriptionPayMethod.LANKAQR_DEEPLINK,
            SubscriptionPayMethod.LANKAQR_SCAN,
            SubscriptionPayMethod.ONEPAY,
        ).forEach {
            assertFalse(ModeBPaymentRules.requiresOwnerConfirmation(it), "$it settles through a bank")
            assertTrue(ModeBPaymentRules.isGatewayDriven(it))
        }
    }

    @Test
    fun only_an_online_transfer_needs_a_slip() {
        assertTrue(ModeBPaymentRules.requiresSlip(SubscriptionPayMethod.ONLINE_TRANSFER))
        SubscriptionPayMethod.entries
            .filterNot { it == SubscriptionPayMethod.ONLINE_TRANSFER }
            .forEach { assertFalse(ModeBPaymentRules.requiresSlip(it)) }
    }

    @Test
    fun cash_is_handed_to_a_collector_and_needs_nothing_on_screen() {
        assertEquals(
            ModeBPaymentStep.HandToCollector,
            ModeBPaymentRules.stepFor(SubscriptionPayMethod.CASH, payment(SubscriptionPayMethod.CASH)),
        )
    }

    @Test
    fun an_online_transfer_shows_the_owners_account_and_asks_for_a_slip() {
        val step = ModeBPaymentRules.stepFor(
            SubscriptionPayMethod.ONLINE_TRANSFER,
            payment(SubscriptionPayMethod.ONLINE_TRANSFER),
        )

        val transfer = assertIs<ModeBPaymentStep.TransferAndUploadSlip>(step)
        assertEquals("1234567890", transfer.payTo.accountNo)
    }

    @Test
    fun a_lankaqr_scan_shows_the_owners_own_bank_qr_image() {
        val step = ModeBPaymentRules.stepFor(
            SubscriptionPayMethod.LANKAQR_SCAN,
            payment(SubscriptionPayMethod.LANKAQR_SCAN),
        )

        val scan = assertIs<ModeBPaymentStep.ShowOwnerLankaQr>(step)
        assertEquals("https://cdn.mageride.lk/qr/1.png", scan.imageUrl)
    }

    @Test
    fun a_deep_link_falls_back_to_the_payload_when_no_bank_app_can_open_it() {
        val initiated = payment(
            SubscriptionPayMethod.LANKAQR_DEEPLINK,
            redirectUrl = "lankaqr://pay?s=1",
            qrPayload = "0002010102",
        )

        assertEquals(
            ModeBPaymentStep.GatewayHandoff(FarePaymentAction.OpenBankApp("lankaqr://pay?s=1")),
            ModeBPaymentRules.stepFor(SubscriptionPayMethod.LANKAQR_DEEPLINK, initiated, bankAppAvailable = true),
        )
        assertEquals(
            ModeBPaymentStep.GatewayHandoff(FarePaymentAction.ShowLankaQrFallback("0002010102")),
            ModeBPaymentRules.stepFor(SubscriptionPayMethod.LANKAQR_DEEPLINK, initiated, bankAppAvailable = false),
        )
    }

    @Test
    fun onepay_opens_its_hosted_page() {
        val step = ModeBPaymentRules.stepFor(
            SubscriptionPayMethod.ONEPAY,
            payment(SubscriptionPayMethod.ONEPAY, redirectUrl = "https://onepay.lk/s/9"),
        )

        assertEquals(ModeBPaymentStep.GatewayHandoff(FarePaymentAction.OpenOnepay("https://onepay.lk/s/9")), step)
    }

    // ----------------------------------------------------------------------------------------
    // AL-49 — no verified payout profile, nowhere to send the money
    // ----------------------------------------------------------------------------------------

    @Test
    fun without_a_verified_payout_profile_there_is_nothing_to_pay_to() {
        // `payTo` is served only from a `verified` org payout profile (BR-31.1); with none, the
        // subscriber must be told rather than shown an empty sheet.
        val unverified = payment(SubscriptionPayMethod.ONLINE_TRANSFER, payTo = null)

        assertEquals(
            ModeBPaymentStep.Unavailable,
            ModeBPaymentRules.stepFor(SubscriptionPayMethod.ONLINE_TRANSFER, unverified),
        )
        val scanWithoutProfile = payment(SubscriptionPayMethod.LANKAQR_SCAN, payTo = null)
        assertEquals(
            ModeBPaymentStep.Unavailable,
            ModeBPaymentRules.stepFor(SubscriptionPayMethod.LANKAQR_SCAN, scanWithoutProfile),
        )
    }

    @Test
    fun a_method_with_no_server_hand_off_is_unavailable_rather_than_broken() {
        assertEquals(
            ModeBPaymentStep.Unavailable,
            ModeBPaymentRules.stepFor(SubscriptionPayMethod.ONEPAY, payment(SubscriptionPayMethod.ONEPAY)),
        )
        assertEquals(
            ModeBPaymentStep.Unavailable,
            ModeBPaymentRules.stepFor(SubscriptionPayMethod.LANKAQR_DEEPLINK, null),
        )
    }

    // ----------------------------------------------------------------------------------------
    // The month's standing
    // ----------------------------------------------------------------------------------------

    @Test
    fun a_transferred_but_unconfirmed_month_is_neither_paid_nor_simply_unpaid() {
        // Telling a subscriber who has already transferred the money that they still owe it would
        // be wrong; so would telling the owner it has arrived.
        assertEquals(
            SubscriberMonthStatus.PENDING_VERIFICATION,
            ModeBPaymentRules.monthStatus(
                payment(SubscriptionPayMethod.ONLINE_TRANSFER, SubscriptionPaymentStatus.PENDING_VERIFICATION),
            ),
        )
    }

    @Test
    fun an_initiated_or_failed_payment_leaves_the_month_unpaid() {
        assertEquals(
            SubscriberMonthStatus.UNPAID,
            ModeBPaymentRules.monthStatus(payment(SubscriptionPayMethod.ONEPAY, SubscriptionPaymentStatus.INITIATED)),
        )
        assertEquals(
            SubscriberMonthStatus.UNPAID,
            ModeBPaymentRules.monthStatus(payment(SubscriptionPayMethod.ONEPAY, SubscriptionPaymentStatus.FAILED)),
        )
        assertEquals(SubscriberMonthStatus.UNPAID, ModeBPaymentRules.monthStatus(null), "no row at all")
    }

    @Test
    fun a_confirmed_payment_marks_the_month_paid() {
        assertEquals(
            SubscriberMonthStatus.PAID,
            ModeBPaymentRules.monthStatus(payment(SubscriptionPayMethod.CASH, SubscriptionPaymentStatus.PAID)),
        )
    }

    @Test
    fun only_a_pending_slip_is_waiting_on_the_owner() {
        assertTrue(
            ModeBPaymentRules.awaitsOwnerAction(
                payment(SubscriptionPayMethod.ONLINE_TRANSFER, SubscriptionPaymentStatus.PENDING_VERIFICATION),
            ),
        )
        assertFalse(
            ModeBPaymentRules.awaitsOwnerAction(
                payment(SubscriptionPayMethod.ONLINE_TRANSFER, SubscriptionPaymentStatus.PAID),
            ),
        )
    }
}
