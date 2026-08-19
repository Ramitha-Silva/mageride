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
// So this is not a workaround for a bad decision in `:shared`; it is the other end of one that was
// made correctly. `Ulid`, `PhoneE164` and `RideVersion` are aliases over `String` and `Int` and need
// nothing here, because Swift already has those names. The two that alias a *class* are the two that
// need re-declaring, and a Swift `typealias` is the exact mirror of the Kotlin one: no wrapper, no
// conversion, no second representation of a date that D-38 has already fixed the meaning of.
//
// **Do not "fix" this by widening the bridge.** Adding an `iosMain` helper per call site, or making
// the primitives real Kotlin classes, would both reverse `Primitives.kt`'s reasoning and put a
// second date type in front of `BusinessCalendar`. The nine `iosMain` helpers this target already
// has exist for things Swift genuinely *cannot* express — a defaulted Kotlin parameter, a `memcpy`,
// an `NSObject` name collision. A name that merely was not emitted is not one of those.

// MARK: - Wire primitives (`data/models/Primitives.kt`)

/// ISO 8601 instant in UTC — the platform's only timestamp form (D3' §0).
///
/// `kotlin.time.Instant`. Swift can *read* one directly (`toEpochMilliseconds()`); it cannot
/// **make** one, which is what `IosInstantKt.parseTimestampOrNull` / `.timestampFromEpochMillis` /
/// `.nowTimestamp` are for. Reach for those rather than `Date()` — a second clock is exactly what
/// `GeoCellSubscription`'s thirty-second hysteresis compares against.
typealias Timestamp = KotlinInstant

/// A business date in `Asia/Colombo` (D-13).
///
/// `kotlinx.datetime.LocalDate`. Never derive one from the handset's zone: `IosBusinessDateKt`'s
/// `colomboBusinessDateNow` / `colomboBusinessDateOf` / `colomboStartOfDayMillis` are the only
/// correct doors, and a date answered from the device's own zone is wrong for five and a half hours
/// a day (D-38).
typealias BusinessDate = Kotlinx_datetimeLocalDate

// MARK: - `SessionState` (`domain/auth`)

// The exporter appends an underscore to break a collision, and the collision is real: `:shared` has
// **two** `SessionState` — an enum, and `domain/auth`'s sealed interface. The enum won the plain
// name, so the sealed interface is `SessionState_` and its cases are `SessionState_SignedIn` and
// friends. Nothing warns about this on either side; the header is the only place it is written down.

/// The signed-in case of `domain/auth`'s `SessionState` — carries a user id, a device id and
/// `isNewUser`, and deliberately never a token (C014).
typealias SessionStateSignedIn = SessionState_SignedIn

/// The case between "phone submitted" and "OTP verified"; carries the challenge SCR-DI-002 counts
/// its five attempts against.
typealias SessionStateAwaitingOtp = SessionState_AwaitingOtp

// MARK: - `MageRideError` (`data/api`)

// Kotlin nests these inside `MageRideError`; the exporter keeps the nesting (`MageRideError.Locked`)
// where this app's Swift writes it flat. Only the branches this target actually matches on are
// aliased — a name nothing uses would be a name nothing keeps in step.
//
// The status→type mapping is `:shared`'s and must not be collapsed here: `409 offer-already-accepted`
// is `Conflict` and `410 offer-expired` is `Gone`, and the two stay distinct all the way to
// `OfferOutcome`.

/// `423` — the OTP gate is locked after five wrong codes (`PackageHandoff`, SCR-DI-016c).
typealias MageRideErrorLocked = MageRideError.Locked

/// No usable connection. One of the three `OnboardingErrors` treats as retryable.
typealias MageRideErrorNetwork = MageRideError.Network

/// The request outlived its budget — retryable, and never a signed-out session (C014: "offline is
/// not revoked").
typealias MageRideErrorTimeout = MageRideError.Timeout

/// The client-side breaker is open, so the request was never sent. Retryable.
typealias MageRideErrorCircuitOpen = MageRideError.CircuitOpen

/// `429` — the caller is being throttled.
typealias MageRideErrorRateLimited = MageRideError.RateLimited

/// `413` — a capture or proof upload exceeded the gateway's body limit.
typealias MageRideErrorPayloadTooLarge = MageRideError.PayloadTooLarge
