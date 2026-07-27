package lk.mageride.shared.data.models

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

// Canonical cross-service enums.
//
// Every one of these matches a database CHECK domain exactly (specs/server_db_schema.md §19,
// specs/D4_mageride_data_model.md §18) and its backend/contracts/_shared.yaml counterpart.
// NO CLIENT-INVENTED VALUES (C012 fence): a value that is not in the CHECK cannot be persisted,
// so offering it in the UI can only ever produce a 400.
//
// Kotlin names are UPPER_SNAKE with an explicit @SerialName wherever the wire form is not already
// upper camel case, so the wire spelling is visible at the declaration rather than implied by a
// naming convention. RideState and PaymentState live in their own files.

/**
 * The ten canonical vehicle types (AL-09), matching the `registry.vehicles.vehicle_type` CHECK.
 *
 * **There is no `car`** — it maps to [SEDAN]. [BUS] and [TRAIN] are Mode A only and are never
 * onboarded through the Driver App; `POST /v1/vehicles` answers `403 mode-not-allowed` for them.
 *
 * @property wire The value as it appears on the wire and in the CHECK constraint.
 */
@Serializable
public enum class VehicleType(public val wire: String) {
    @SerialName("motorbike")
    MOTORBIKE("motorbike"),

    @SerialName("three_wheeler")
    THREE_WHEELER("three_wheeler"),

    @SerialName("flex")
    FLEX("flex"),

    @SerialName("sedan")
    SEDAN("sedan"),

    @SerialName("mini_van")
    MINI_VAN("mini_van"),

    @SerialName("van")
    VAN("van"),

    @SerialName("truck")
    TRUCK("truck"),

    @SerialName("mini_truck")
    MINI_TRUCK("mini_truck"),

    @SerialName("bus")
    BUS("bus"),

    @SerialName("train")
    TRAIN("train"),
    ;

    /** Whether this type can be booked as a Mode C ride — see [RideVehicleType]. */
    public val isRideBookable: Boolean get() = this != BUS && this != TRAIN
}

/**
 * The subset of [VehicleType] bookable as a Mode C ride
 * (`_shared.yaml#/components/schemas/RideVehicleType`).
 *
 * The passenger types plus `truck`/`mini_truck`, which are **delivery-only** (AL-09, Epic 20).
 * It is a separate enum rather than a runtime filter because the contract makes it one: every
 * booking-side field is typed against it, so a request carrying `bus` will not compile.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class RideVehicleType(public val wire: String) {
    @SerialName("motorbike")
    MOTORBIKE("motorbike"),

    @SerialName("three_wheeler")
    THREE_WHEELER("three_wheeler"),

    @SerialName("flex")
    FLEX("flex"),

    @SerialName("sedan")
    SEDAN("sedan"),

    @SerialName("mini_van")
    MINI_VAN("mini_van"),

    @SerialName("van")
    VAN("van"),

    @SerialName("truck")
    TRUCK("truck"),

    @SerialName("mini_truck")
    MINI_TRUCK("mini_truck"),
    ;

    /** Whether this type is package-delivery only (AL-09). */
    public val isDeliveryOnly: Boolean get() = this == TRUCK || this == MINI_TRUCK

    /** Widens to the canonical enum. Total — every ride type is a vehicle type. */
    public fun toVehicleType(): VehicleType = VehicleType.entries.first { it.wire == wire }

    public companion object {
        /** Narrows a [VehicleType], or `null` for `bus` / `train`. */
        public fun from(vehicleType: VehicleType): RideVehicleType? =
            entries.firstOrNull { it.wire == vehicleType.wire }
    }
}

/**
 * The three operating modes (`_shared.yaml#/components/schemas/OperatingMode`, DB `mode`).
 *
 * - [A] — scheduled public transport (bus, train). Free: no daily fee is charged.
 * - [B] — shared private vehicle, visible to entitled subscribers only.
 * - [C] — on-demand ride.
 *
 * Mode A/B tracking sessions belong to **trip-state-svc**; Mode C rides belong to **ride-svc**,
 * and that boundary is never crossed (CLAUDE.md, R-01). `POST /v1/sessions/start` rejects [C].
 */
@Serializable
public enum class ServiceMode {
    A,
    B,
    C,
    ;

    /** Modes a tracking session may be started in (R-01). */
    public val isTrackingSessionMode: Boolean get() = this != C
}

/**
 * The nine canonical roles (AL-06), matching the `iam.users.role` / `iam.user_roles` CHECK.
 *
 * Effective permissions are the **union** of a user's granted roles, evaluated deny-by-default.
 * There is deliberately **no `reseller`** — AL-01 makes bulk credit a capability of any driver,
 * not a role.
 *
 * @property wire The value as it appears in the `role` JWT claim and on the wire.
 */
@Serializable
public enum class Role(public val wire: String) {
    @SerialName("passenger")
    PASSENGER("passenger"),

    @SerialName("driver")
    DRIVER("driver"),

    @SerialName("fleet_owner")
    FLEET_OWNER("fleet_owner"),

    @SerialName("admin")
    ADMIN("admin"),

    @SerialName("super_admin")
    SUPER_ADMIN("super_admin"),

    @SerialName("verification_officer")
    VERIFICATION_OFFICER("verification_officer"),

    @SerialName("support_csr")
    SUPPORT_CSR("support_csr"),

    @SerialName("finance_officer")
    FINANCE_OFFICER("finance_officer"),

    @SerialName("auditor")
    AUDITOR("auditor"),
    ;

    /** Whether this is a staff role. Internal roles never sign in with phone OTP (AL-07). */
    public val isInternal: Boolean
        get() = this !in setOf(PASSENGER, DRIVER, FLEET_OWNER)
}

/**
 * Org-scoped sub-role carried in the `fleet_role` JWT claim (AL-03), matching the
 * `iam.fleet_members.fleet_role` CHECK.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class FleetRole(public val wire: String) {
    @SerialName("owner")
    OWNER("owner"),

    @SerialName("manager")
    MANAGER("manager"),

    @SerialName("viewer")
    VIEWER("viewer"),
}

/**
 * Sinhala, Tamil or English (`_shared.yaml#/components/schemas/LanguageCode`, DB `language`).
 *
 * Every user-facing string on the platform exists in all three (D-26, CLAUDE.md "Trilingual
 * resources"). This enum is the *selector*; the strings themselves live in each app's resource
 * files and never in this module.
 *
 * @property wire The ISO 639-1 code used in `?lang=` and in `iam.users.language`.
 */
@Serializable
public enum class Language(public val wire: String) {
    @SerialName("si")
    SI("si"),

    @SerialName("ta")
    TA("ta"),

    @SerialName("en")
    EN("en"),
    ;

    public companion object {
        /**
         * Server-side fallback when a requested language is absent or unsupported (D3' §0).
         *
         * The resolution order is: `?lang=` → the caller's profile language → this.
         */
        public val FALLBACK: Language = EN

        /** Resolves an ISO code, or `null` when it is not one of the three. */
        public fun fromWire(wire: String): Language? = entries.firstOrNull { it.wire == wire }
    }
}

/**
 * Package size band (`ride.yaml#/components/schemas/PackageSize`, `rides.rides.package_size`).
 *
 * Small, medium, large. Set at booking time on a `package` ride and never afterwards.
 */
@Serializable
public enum class PackageSize {
    S,
    M,
    L,
}

/**
 * The kind of a stored document (`registry.documents.kind` CHECK, C003).
 *
 * `revenue_license` is present because AL-50 names it as one of the four SCR-FP-004 slots and the
 * AL-10 approval gate needs it — `server_db_schema.md` §2 omits it, D4' §2 has it, and C003 took
 * D4'.
 *
 * @property wire The value as it appears on the wire and in the CHECK constraint.
 */
@Serializable
public enum class DocumentKind(public val wire: String) {
    @SerialName("driving_license")
    DRIVING_LICENSE("driving_license"),

    @SerialName("registration")
    REGISTRATION("registration"),

    @SerialName("permit")
    PERMIT("permit"),

    @SerialName("insurance")
    INSURANCE("insurance"),

    @SerialName("revenue_license")
    REVENUE_LICENSE("revenue_license"),
}

/**
 * Expiry state of a stored document (`registry.documents.status` CHECK, E-03).
 *
 * [EXPIRED] is what auto-suspends a vehicle's dispatch state; [EXPIRING] is the warning window
 * the driver is nudged in.
 */
@Serializable
public enum class DocumentStatus {
    VALID,
    EXPIRING,
    EXPIRED,
    REJECTED,
}

/**
 * How a call between ride participants is placed (`comms.call_log.call_type` CHECK, AL-48).
 *
 * - [FREE_VOIP] — a LiveKit session brokered by voip-svc.
 * - [DIRECT_DIAL] — the client dialled `tel:` itself and logged it afterwards. Best-effort: a
 *   `tel:` dial cannot be server-verified.
 *
 * **`normal_masked` was removed by AL-48** together with the masked PSTN bridge, the DID pool and
 * the masked-SMS relay. It can never appear here.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class CallType(public val wire: String) {
    @SerialName("free_voip")
    FREE_VOIP("free_voip"),

    @SerialName("direct_dial")
    DIRECT_DIAL("direct_dial"),
}

/**
 * Client platform (`_shared.yaml#/components/parameters/XPlatform`, `iam.devices.platform`).
 *
 * Sent as `X-Platform` on every app-originated request, alongside `X-App-Version`, and read by
 * the gateway's minimum-version gate (D-31).
 *
 * @property wire The value as it appears in the header and on the wire.
 */
@Serializable
public enum class ClientPlatform(public val wire: String) {
    @SerialName("android")
    ANDROID("android"),

    @SerialName("ios")
    IOS("ios"),
}

/**
 * Which app a session belongs to (`iam.sessions.app` CHECK, AL-08).
 *
 * One active device **per app**, so a driver session and a passenger session can coexist on the
 * same handset and a new-device login revokes only that app's prior session (US-1.12).
 *
 * @property wire The value as it appears in the `app` JWT claim and on the wire.
 */
@Serializable
public enum class AppSurface(public val wire: String) {
    @SerialName("passenger")
    PASSENGER("passenger"),

    @SerialName("driver")
    DRIVER("driver"),
}
