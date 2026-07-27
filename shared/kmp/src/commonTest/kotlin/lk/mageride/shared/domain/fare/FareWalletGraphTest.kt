package lk.mageride.shared.domain.fare

import lk.mageride.shared.di.sharedModules
import lk.mageride.shared.domain.wallet.DailyFeeSchedule
import lk.mageride.shared.domain.wallet.VoucherCatalogue
import kotlin.test.Test
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * The C016 slice of the Koin graph — which is deliberately empty.
 *
 * The tests exist to make that a decision rather than an omission: [fareWalletModule] is in
 * [sharedModules] so no app needs an edit when a later component gives it something to bind, and it
 * holds nothing today because every input this component computes from is server-supplied and
 * admin-tunable. Binding a tariff, a fee tier or a voucher ladder would pin whatever the numbers
 * were when the app launched.
 */
class FareWalletGraphTest {

    @Test
    fun the_module_is_registered_with_the_shared_graph() {
        assertTrue(fareWalletModule in sharedModules, "the apps use sharedModules and nothing else")
    }

    @Test
    fun every_money_rule_is_constructible_from_a_config_without_the_graph() {
        // This is the claim the empty module rests on: a screen that has just read the admin
        // config builds these itself, so nothing has to be re-fetched from a container holding a
        // stale copy.
        assertNotNull(FareCalculator(TariffTable.D5_DEFAULTS, SurchargeWindows.D5_DEFAULTS))
        assertNotNull(DailyFeeSchedule.D5_DEFAULTS)
        assertNotNull(VoucherCatalogue(emptyList()))
    }
}
