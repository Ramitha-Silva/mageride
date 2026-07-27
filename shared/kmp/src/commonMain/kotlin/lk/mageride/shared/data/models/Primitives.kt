package lk.mageride.shared.data.models

import kotlinx.datetime.LocalDate
import kotlin.time.Instant

// Wire primitives shared by every MageRide DTO.
//
// Source: backend/contracts/_shared.yaml#/components/schemas — Ulid, PhoneE164, PhoneMasked,
// Timestamp, BusinessDate; plus ride.yaml's Version.
//
// These are TYPE ALIASES, NOT VALUE CLASSES, and deliberately so: :shared is compiled into an
// XCFramework and consumed from Swift, where a Kotlin `value class` is boxed or erased at the
// Objective-C boundary. An alias costs nothing at runtime, reads correctly in Kotlin, and reaches
// Swift as the underlying String / Instant / LocalDate.
//
// Validation of the underlying patterns (^\+947\d{8}$, the ULID/UUID charset) belongs to the
// gateway and the services — a client that silently rewrote a server-supplied identifier would be
// a worse failure than one that passed it through.

/**
 * ULID or UUID identifier, rendered canonically (26–36 chars).
 *
 * Every MageRide id on the wire is one of these; the server chooses which form per aggregate.
 */
public typealias Ulid = String

/** Sri Lankan mobile number in E.164 — `+947XXXXXXXX`. */
public typealias PhoneE164 = String

/**
 * Role-masked MSISDN for directory reads (AL-40/41/42) — `+9477*****67`.
 *
 * Never parse this back into a dialable number. The clear number for a ride counterparty is
 * `RideDetail.counterpartyPhone` (AL-48), which is a [PhoneE164] and is only present from
 * `Accepted` onward.
 */
public typealias PhoneMasked = String

/**
 * ISO 8601 instant in UTC — the platform's only timestamp form (D3' §0).
 *
 * `kotlin.time.Instant` serialises to and parses exactly the `2026-07-27T04:15:00Z` shape the
 * contracts print, so no custom serializer is involved.
 */
public typealias Timestamp = Instant

/**
 * A business date in `Asia/Colombo` (D-13).
 *
 * D-38: every business date the platform persists also records the instant it was derived at, so
 * a DTO carrying one of these normally carries a `…TzAt` [Timestamp] beside it. The pair is not
 * decorative — near midnight the two disagree, and support needs to know which day the client was
 * shown.
 */
public typealias BusinessDate = LocalDate

/**
 * Optimistic-concurrency version of a ride aggregate (`ride.yaml#/components/schemas/Version`).
 *
 * Every ride response carries it; every ride mutation echoes it back and fails
 * `409 version-conflict` if the aggregate has moved on (R-14).
 */
public typealias RideVersion = Int
