package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.wallet.Wallet

/**
 * What the wallet should be telling the driver right now (US-9.9, D5' §9.4), from one read (C088).
 *
 * **Why a wrapper at all.** [WalletRules.alertFor] takes the low-balance threshold as a **defaulted**
 * parameter, and Kotlin default arguments do not survive the Objective-C export — every parameter
 * becomes required. A Swift caller therefore has to supply the threshold, and the only two ways to do
 * that are to spell Rs 200 in Swift, which is a second copy of the number §9.4 makes
 * admin-configurable, or to read [WalletRules.DEFAULT_LOW_BALANCE_THRESHOLD] back across the bridge to
 * hand it straight back. One function is better than either — the same argument
 * `colomboBusinessDate` and `rideProjectionOf` make.
 *
 * **The default is deliberately applied here and not passed through.** `DEFAULT_LOW_BALANCE_THRESHOLD`
 * is documented as a fallback *"for a client that has not read the config yet"*, and that is exactly
 * what a driver app is today: `wallet.yaml` carries no read for the threshold. When one exists, this
 * function grows a parameter and SCR-DI-010's banner is the only caller that has to change.
 *
 * @param wallet `GET /v1/wallet/{userId}` as the dashboard read it.
 */
public fun walletAlertFor(wallet: Wallet): WalletAlert = WalletRules.alertFor(WalletStanding.of(wallet))
