package lk.mageride.shared.domain.fare

import org.koin.core.module.Module
import org.koin.dsl.module

/**
 * The C016 slice of the Koin graph — **deliberately empty**.
 *
 * C015's module binds one thing, `OfferSession`, because a driver's single offer slot is genuinely
 * stateful and genuinely lives as long as the app process. C016 has no such object, and the reason
 * is worth stating rather than leaving to be rediscovered.
 *
 * **Every input this component computes from is admin-tunable and server-supplied.** The tariff
 * table is versioned by `effective_from` and never updated in place; the peak and night windows,
 * the seven daily-fee tiers, the voucher discount ladder and the low-balance threshold are all
 * rows an operator edits in the Admin Portal. A binding for any of them would pin whatever the
 * numbers were the first time the app launched — which is precisely the failure C015 called out
 * for `DirectionalPredicate`, except that here it would apply to *money*. So [TariffTable],
 * [SurchargeWindows], [FareCalculator],
 * [lk.mageride.shared.domain.wallet.DailyFeeSchedule] and
 * [lk.mageride.shared.domain.wallet.VoucherCatalogue] are all constructed at the call site from
 * the config just read, and everything else in the component — [FareRounding],
 * [PaymentTransitions], [PaymentMethods],
 * [lk.mageride.shared.domain.wallet.CreditTransferRules],
 * [lk.mageride.shared.domain.subscription.ModeBBilling] — is a pure function or a value type.
 *
 * The two projections that do hold state, [PaymentProjection] and
 * [lk.mageride.shared.domain.wallet.WalletHistory], are **per ride** and **per screen**
 * respectively. A singleton of either would be a bug: two rides settling at once is ordinary, and
 * a history that outlived its screen would keep deduplicating against lines nobody is looking at.
 *
 * The module exists anyway, and is registered in `sharedModules`, so that the four apps never need
 * an edit when a later component gives C016 something to bind (`shared/kmp/CLAUDE.md`, "Dependency
 * rules").
 */
public val fareWalletModule: Module = module {
    // Intentionally no bindings — see the KDoc above.
}
