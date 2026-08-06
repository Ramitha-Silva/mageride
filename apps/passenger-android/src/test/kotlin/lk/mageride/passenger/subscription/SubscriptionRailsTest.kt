package lk.mageride.passenger.subscription

import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.domain.subscription.ModeBPaymentRules
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * AL-59, as a table nobody can quietly widen.
 *
 * The wireframe for SCR-PA-025a still draws **OnePay · cards / wallets · +5 %**, and rebuilding it
 * from that drawing is the mistake this test exists to catch: a Mode B subscription is paid to the
 * **fleet owner**, OnePay has one merchant account per merchant, and the money would land in
 * MageRide's. The rail was removed from `subscription.yaml` along with its webhook.
 */
class SubscriptionRailsTest {

    @Test
    fun the_sheet_offers_four_rails_and_onepay_is_not_one_of_them() {
        assertEquals(
            listOf(
                SubscriptionPayMethod.LANKAQR_DEEPLINK,
                SubscriptionPayMethod.LANKAQR_SCAN,
                SubscriptionPayMethod.ONLINE_TRANSFER,
                SubscriptionPayMethod.CASH,
            ),
            SubscriptionRails.METHODS,
            "D2' §16e's four modes, in the wireframe's order",
        )

        assertFalse(SubscriptionPayMethod.ONEPAY in SubscriptionRails.METHODS, "AL-59 removed the OnePay rail")
        assertEquals(setOf(SubscriptionPayMethod.ONEPAY), SubscriptionRails.RETIRED)
    }

    @Test
    fun every_declared_method_has_copy_including_the_retired_one() {
        // `SubscriptionPayMethod` types the whole `subscription.payments.method` domain, and
        // SCR-PA-025b renders history rows written before AL-59 — so a `when` that omitted OnePay
        // would not compile, and one that fell through to a generic string would print an empty
        // method on a real statement.
        SubscriptionPayMethod.entries.forEach { method ->
            assertTrue(SubscriptionRails.label(method) != 0, "$method has no label")
            assertTrue(SubscriptionRails.caption(method) != 0, "$method has no caption")
        }
    }

    @Test
    fun two_of_the_four_rails_settle_on_the_owner_and_not_on_a_gateway() {
        // BR-23.10, and the reason `pending_verification` is a first-class status: a passenger who
        // has already transferred the money must not be told they have not paid.
        assertTrue(ModeBPaymentRules.requiresOwnerConfirmation(SubscriptionPayMethod.ONLINE_TRANSFER))
        assertTrue(ModeBPaymentRules.requiresOwnerConfirmation(SubscriptionPayMethod.CASH))

        assertFalse(ModeBPaymentRules.requiresOwnerConfirmation(SubscriptionPayMethod.LANKAQR_DEEPLINK))
        assertFalse(ModeBPaymentRules.requiresOwnerConfirmation(SubscriptionPayMethod.LANKAQR_SCAN))

        // US-23.4 — only the transfer needs a slip. Cash has nothing to photograph.
        assertTrue(ModeBPaymentRules.requiresSlip(SubscriptionPayMethod.ONLINE_TRANSFER))
        assertFalse(ModeBPaymentRules.requiresSlip(SubscriptionPayMethod.CASH))
    }
}
