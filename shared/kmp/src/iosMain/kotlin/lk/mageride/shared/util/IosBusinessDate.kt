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

/**
 * The Asia/Colombo business date [at] falls on, as a [BusinessDate] (C090).
 *
 * The typed counterpart of [colomboBusinessDate] for an **arbitrary** instant, completing the trio
 * with [colomboBusinessDateNow]. Same reason all three exist: [BusinessCalendar.businessDate] takes
 * its zone as a defaulted parameter and a default argument does not survive the export, so a Swift
 * caller would otherwise have to hold a `kotlinx.datetime.TimeZone` — or name `"Asia/Colombo"` a
 * second time — to ask which Colombo day a server timestamp fell on.
 */
public fun colomboBusinessDateOf(at: Timestamp): BusinessDate = BusinessCalendar.businessDate(at)

/**
 * `Asia/Colombo` — the tz-database identifier D-38 fixes, for a Foundation `TimeZone` (C090).
 *
 * SCR-DI-017's *"Today · 11.2 km · Sedan"*, SCR-DI-018's countdown and SCR-DI-020's chart buckets
 * are all read on a **Colombo** calendar and none of them is a business *date* — an hour of the
 * day, a wall-clock reading and "is this the same day as that" are `Calendar` and `DateFormatter`
 * questions, which on this platform take a `Foundation.TimeZone` rather than a `BusinessDate`.
 *
 * A string rather than [BusinessCalendar.ZONE] itself, so nothing on the Swift side has to hold a
 * `kotlinx.datetime.TimeZone` to build a Foundation one out of it — and, more to the point, so the
 * zone is still written **once**, here in `:shared`, rather than as an `"Asia/Colombo"` literal in
 * an app. A driver whose handset came back from a trip abroad still on its old zone otherwise reads
 * a board sorted correctly and labelled five and a half hours wrong.
 */
public fun colomboZoneId(): String = BusinessCalendar.ZONE.id

/**
 * Midnight at the start of [date] in Colombo, as epoch milliseconds (C090).
 *
 * [BusinessCalendar.startOfDay] takes its zone as a **defaulted** parameter, and a default argument
 * does not survive the export — see [colomboBusinessDate]. The result is milliseconds rather than a
 * `Timestamp` because its one caller turns it into a `Date` immediately: SCR-DI-020's chart walks
 * the days of the window `EarningsSummary.rangeFrom`/`rangeTo` describe, and the window's ends are
 * `BusinessDate`s the server chose.
 */
public fun colomboStartOfDayMillis(date: BusinessDate): Long = BusinessCalendar.startOfDay(date).toEpochMilliseconds()
