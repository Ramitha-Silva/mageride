import MageRideShared

// The Kotlin names this app writes, mapped onto the spellings the XCFramework actually exports.
//
// ### Why this file exists
//
// `:shared` names two wire primitives with a Kotlin `typealias` — `Timestamp = kotlin.time.Instant`
// and `BusinessDate = kotlinx.datetime.LocalDate` (`data/models/Primitives.kt`). That file chose
// aliases over value classes deliberately, and says so: a `value class` is boxed or erased at the
// Objective-C boundary, whereas an alias "costs nothing at runtime, reads correctly in Kotlin, and
// reaches Swift as the underlying String / Instant / LocalDate".
//
// Both halves of that are true, and the second half is the trap. The **value** crosses intact; the
// **name** does not. A Kotlin typealias is erased by the Objective-C exporter — it is not a
// declaration, so there is nothing to emit — and what appears in `MageRideShared.h` is the
// underlying class under its own exported spelling: `KotlinInstant` and `Kotlinx_datetimeLocalDate`.
// Swift never sees `Timestamp` or `BusinessDate` at all.
//
// `Ulid`, `PhoneE164` and `RideVersion` are aliases over `String` and `Int` and need nothing here,
// because Swift already has those names. The two that alias a *class* are the two that need
// re-declaring, and a Swift `typealias` is the exact mirror of the Kotlin one.
//
// **A near-copy of `apps/driver-ios/DriverApp/DI/SharedTypeNames.swift`, and deliberately not
// shared with it.** The two apps are separate Swift modules with no code in common — the only thing
// they share is the XCFramework — so a common file would have to become a third SPM target for six
// typealiases. Each app aliases only the names it actually writes: this one omits driver-only
// branches like `MageRideError.Locked` (SCR-DI-016c's OTP gate), because a name nothing uses is a
// name nothing keeps in step.
//
// **Do not "fix" this by widening the bridge.** Adding an `iosMain` helper per call site, or making
// the primitives real Kotlin classes, would both reverse `Primitives.kt`'s reasoning. The `iosMain`
// helpers exist for things Swift genuinely *cannot* express — a defaulted Kotlin parameter, a
// `memcpy`, a name that collides with `NSObject`'s. A name that merely was not emitted is not one.

// MARK: - Wire primitives (`data/models/Primitives.kt`)

/// ISO 8601 instant in UTC — the platform's only timestamp form (D3' §0).
///
/// `kotlin.time.Instant`. Swift can *read* one directly (`toEpochMilliseconds()`); it cannot
/// **make** one, which is what `IosInstantKt.parseTimestampOrNull` / `.timestampFromEpochMillis` /
/// `.nowTimestamp` are for. Reach for those rather than `Date()` — a second clock is exactly what
/// `GeoCellSubscription`'s thirty-second hysteresis compares against, and on this app that
/// subscription is the live map.
typealias Timestamp = KotlinInstant

/// A business date in `Asia/Colombo` (D-13).
///
/// `kotlinx.datetime.LocalDate`. Never derive one from the handset's zone: `IosBusinessDateKt`'s
/// `colomboBusinessDateNow` / `colomboBusinessDateOf` / `colomboStartOfDayMillis` are the only
/// correct doors, and a date answered from the device's own zone is wrong for five and a half hours
/// a day (D-38).
typealias BusinessDate = Kotlinx_datetimeLocalDate

// MARK: - `SessionState` (`domain/auth`)

// **The trailing-underscore rule, which is worth knowing beyond this one type.** Kotlin packages
// keep two same-named declarations apart; the Objective-C export has one flat namespace and cannot,
// so when two Kotlin types share a simple name the exporter gives the plain name to one of them and
// appends an underscore to the other. Nothing warns on either side, and the loser is not always the
// one you would guess — `MageRideShared.h` is the only place the outcome is written down. Check it
// before assuming a `:shared` type is reachable by its Kotlin name.
//
// Two of them reach this app:
//
//   * `SessionState` — an enum, and `domain/auth`'s sealed interface. The enum won, so the interface
//     is `SessionState_` and its cases are `SessionState_SignedIn` and friends. Aliased below,
//     because this app matches on the cases in several places.
//
//   * `ModeBBilling` — `data/models/registry`'s enum, and `domain/subscription`'s rules object. The
//     enum won, so the rules are reached as **`ModeBBilling_.shared`**, which is what the three
//     `Subscription/*Model.swift` files spell. Deliberately NOT aliased: an alias would have to
//     invent a name for a Kotlin object that already has a good one, and the underscore at the call
//     site is a true statement about the bridge.

/// The signed-in case of `domain/auth`'s `SessionState` — carries a user id, a device id and
/// `isNewUser`, and deliberately never a token (C014).
typealias SessionStateSignedIn = SessionState_SignedIn

/// The case between "phone submitted" and "OTP verified"; carries the challenge SCR-PI-002 counts
/// its five attempts against.
typealias SessionStateAwaitingOtp = SessionState_AwaitingOtp

// MARK: - `MageRideError` (`data/api`)

// Kotlin nests these inside `MageRideError`; the exporter keeps the nesting (`MageRideError.Network`)
// where this app's Swift writes it flat.
//
// The status→type mapping is `:shared`'s and must not be collapsed here: `409 offer-already-accepted`
// is `Conflict` and `410 offer-expired` is `Gone`, and the two stay distinct all the way to
// `OfferOutcome`.

/// No usable connection. One of the three this app treats as retryable rather than as a failed
/// booking — R-18 dedupes on `clientRequestId`, so a retry after one of these is a replay.
typealias MageRideErrorNetwork = MageRideError.Network

/// The request outlived its budget — retryable, and never a signed-out session (C014: "offline is
/// not revoked").
typealias MageRideErrorTimeout = MageRideError.Timeout

/// The client-side breaker is open, so the request was never sent. Retryable.
typealias MageRideErrorCircuitOpen = MageRideError.CircuitOpen

/// `429` — the caller is being throttled.
typealias MageRideErrorRateLimited = MageRideError.RateLimited
