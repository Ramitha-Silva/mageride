package lk.mageride.shared.domain.dispatch

import lk.mageride.shared.data.models.dispatch.DriverLevelResponse
import lk.mageride.shared.data.models.dispatch.ScheduledRide

// Reaching the Job Board's two server-owned numbers from Swift (C090).
//
// WHY THESE EXIST. Both are the same reason `colomboBusinessDate` and `walletAlertFor` exist: a
// Kotlin **default argument does not survive the Objective-C export**, so a Swift call site has to
// supply the value the spec has already fixed — which is a second copy of it, in the one language
// that cannot be checked against D5' by any test in this module.
//
//  * [JobBoard.goesLiveAt] is a `Timestamp` and Swift can read one, but `timeToGoLive` answers a
//    `Duration` — an inline value class the export flattens into an opaque `Long` whose encoding is
//    a packed nanos/millis pair with a tag bit, not a nanosecond count. Reading it on the Swift side
//    would be arithmetic on an implementation detail. So the T-30 instant crosses as epoch millis
//    and the comparison is an ordinary one; `JobBoard.GO_LIVE_LEAD` stays the only place the
//    thirty minutes is written (D5' §3.7, US-6A.5, and §14.4's `SCHEDULED_REMINDER`, which is the
//    same instant).
//  * [DriverLevelRules] takes a `LevelConfig` that defaults to [DriverLevelRules.D5_DEFAULTS], and
//    a client is meant to *override one field of it* with the server's own `levelUpThreshold`
//    (US-14.12, `PUT /v1/admin/drivers/level-config`). From Swift that is a four-argument `doCopy`
//    on a companion constant, three of whose arguments have to be read back off the same constant
//    to hand straight in again — or, worse, D5' §4.2's 500 and its jobBoardMinLevel 2 typed into a
//    Swift file. This is `apps/driver-android/.../jobs/JobsRepository.kt`'s `JobStanding.rules`,
//    written once on this side of the bridge instead.

/**
 * When [ride] leaves the Job Board and dispatch starts offering it, as epoch milliseconds
 * (D5' §3.7).
 *
 * The **same instant** SCR-DI-018's 30-minute reminder is due at (US-6A.15, D5' §14.4), which is
 * why both driver screens read this one function rather than each keeping a threshold: a board row
 * that has expired and an upcoming ride whose reminder has fired are one fact seen from two lists.
 */
public fun jobBoardGoesLiveAtMillis(ride: ScheduledRide): Long = JobBoard().goesLiveAt(ride).toEpochMilliseconds()

/**
 * The level rules to evaluate a driver against, honouring the server's own threshold when it sent
 * one (US-14.12).
 *
 * `null` — a level read that did not answer — falls back to [DriverLevelRules.D5_DEFAULTS], which
 * is what the rules already do for a client that has not been told otherwise. **That is not the
 * same as guessing a driver's level**: the rules say what a level *costs* and which level opens the
 * board, and neither depends on knowing where this driver is. Who may see the board is
 * [DriverLevelRules.hasJobBoardAccess] against a standing, and a standing that could not be read
 * stays `null` all the way to the screen (US-6A.8).
 */
public fun driverLevelRulesFor(level: DriverLevelResponse?): DriverLevelRules {
    val threshold = level?.levelUpThreshold ?: return DriverLevelRules()
    return DriverLevelRules(DriverLevelRules.D5_DEFAULTS.copy(levelUpThreshold = threshold))
}
