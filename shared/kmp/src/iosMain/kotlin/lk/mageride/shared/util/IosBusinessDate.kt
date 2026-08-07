package lk.mageride.shared.util

import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Timestamp
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * The Asia/Colombo calendar date [at] falls on, as `YYYY-MM-DD` (D-38).
 *
 * **Why a wrapper at all.** [BusinessCalendar.businessDate] answers a `kotlinx.datetime.LocalDate`
 * and takes its zone as a **defaulted** parameter — and Kotlin default arguments do not survive the
 * Objective-C export, so a Swift caller has to supply a `Kotlinx_datetimeTimeZone` of its own to
 * reach it. The only two ways to produce one on that side are to name `"Asia/Colombo"` in Swift,
 * which is a second copy of the zone D-38 fixes exactly once, or to read `BusinessCalendar.ZONE`
 * back across the bridge to hand it straight back. Both are worse than one function.
 *
 * The result is a **string** rather than a `LocalDate` for the same reason: what SCR-DI-026 draws is
 * `Lanka Fleet (Pvt) Ltd · until 2026-06-30`, and `LocalDate.toString()` is already ISO-8601. It is
 * `apps/driver-android`'s `BusinessCalendar.businessDate(until).toString()`, byte for byte, which is
 * what keeps the two apps' captions the same string.
 *
 * The zone itself is not restated here and must not be: a driver in another timezone must read the
 * expiry the fleet set, not the one their handset is on.
 *
 * @param at The instant to resolve — `VehicleSummary.assignedUntil`, a `fee_date`, a `next_due`.
 */
public fun colomboBusinessDate(at: Timestamp): String = BusinessCalendar.businessDate(at).toString()

/**
 * Today's Asia/Colombo business date, as a [BusinessDate] (C088).
 *
 * The **typed** counterpart of [colomboBusinessDate], and the only way Swift has to produce one at
 * all: `BusinessDate` is `kotlinx.datetime.LocalDate`, whose constructors and `parse` reach the
 * bridge under compiler-generated names that are an implementation detail rather than a contract.
 *
 * *Today* rather than an arbitrary date, because that is the only one an app has any business
 * minting: a `fee_date`, a `period_month` and a `next_due` all arrive from the server, and the one
 * question a client asks for itself is *"which Colombo day is it now"* — which D-38 fixes in
 * Asia/Colombo and nowhere else. Answered from [BusinessCalendar] so the zone is still written once.
 */
@OptIn(ExperimentalTime::class)
public fun colomboBusinessDateNow(): BusinessDate = BusinessCalendar.businessDate(Clock.System.now())
