package lk.mageride.e2e

/**
 * Everything the run needs from outside itself. `run.sh` sets all of it; the defaults describe the
 * stack `infra/docker-compose.skeleton.yml` brings up on this host.
 */
internal data class Environment(
    /** The API gateway. Every HTTP call goes through it — nothing here talks to a service directly. */
    val gatewayUrl: String = env("MAGERIDE_GATEWAY", "http://127.0.0.1:5000"),

    /** EMQX's plaintext listener, published on loopback by the slim stack. */
    val mqttHost: String = env("MAGERIDE_MQTT_HOST", "127.0.0.1"),
    val mqttPort: Int = env("MAGERIDE_MQTT_PORT", "1883").toInt(),

    /**
     * The HMAC secret EMQX validates session tokens with.
     *
     * The harness mints its own because **`POST /v1/auth/mqtt-token` does not exist yet** — C020
     * left it to C026, and `Iam.Api/Endpoints/AuthEndpoints.cs` says so. When C026 lands, this
     * whole path should be replaced by a call to it: minting a device credential client-side is
     * exactly what E-02 does not want in a real client.
     */
    val mqttSecret: String = env("MAGERIDE_MQTT_SECRET", "mageride-dev-mqtt-jwt-secret-change-me"),

    /** Redpanda's external listener. Read for `offer.created` — see [OfferWatcher] for why. */
    val kafkaBootstrap: String = env("MAGERIDE_KAFKA", "127.0.0.1:19092"),

    /**
     * How to read the dev SMS log.
     *
     * C020's `DevLoggingOtpSender` writes the OTP to iam-svc's log at Information — its KDoc names
     * this run as the reason it exists. There is no other way in: the OTP is not in the
     * `POST /v1/auth/otp/request` response, and it must not be.
     */
    val otpLogCommand: String = env(
        "MAGERIDE_OTP_LOG_CMD",
        "docker compose -f infra/docker-compose.skeleton.yml logs --no-log-prefix --since 120s iam-svc",
    ),

    /** The driver `db/seed/skeleton.sql` creates, with the vehicle it selected as live (C021). */
    val driverPhone: String = env("MAGERIDE_DRIVER_PHONE", "+94770000001"),
    val driverId: String = env("MAGERIDE_DRIVER_ID", "00000000-0000-4000-8000-00000000d001"),
    val vehicleId: String = env("MAGERIDE_VEHICLE_ID", "00000000-0000-4000-8000-00000000c001"),

    /**
     * The passenger. Not seeded — iam-svc creates the account on first successful OTP verify, which
     * is the flow a real passenger takes and therefore the one worth exercising.
     *
     * **A fresh number per run by default**, and that is not fastidiousness. R-02 allows one live
     * ride per passenger, and `POST /v1/rides/{rideId}/cancel` **does not exist yet** — C022 shipped
     * the happy path and R-03/R-15/R-16 cancellation is C035 — so a run that dies mid-ride leaves a
     * ride the platform offers no way to clear, and every later run for that number answers
     * `409 active-ride-exists`. A new passenger sidesteps a gap this component cannot close.
     * `run.sh` also tears the volumes down, which is the other half.
     */
    val passengerPhone: String = env("MAGERIDE_PASSENGER_PHONE", newPassengerPhone()),

    /**
     * A second passenger, who books the ride that is deliberately left to expire.
     *
     * Two are needed because R-02 allows one live ride per passenger and the expiry ride never
     * reaches a terminal state — see the class KDoc on [Run] for why the expiry cannot be shown on
     * the same ride that is driven to `PaymentPending`.
     */
    val secondPassengerPhone: String = env("MAGERIDE_PASSENGER_PHONE_2", newPassengerPhone(offset = 1)),
) {
    internal companion object {
        fun env(name: String, default: String): String =
            System.getenv(name)?.takeIf { it.isNotBlank() } ?: default

        /**
         * A Sri Lankan mobile number no earlier run has used.
         *
         * Seconds since the epoch, low seven digits — inside `+947` and comfortably clear of the
         * `+9477000000x` block `db/seed/skeleton.sql` reserves for the seeded driver.
         */
        private fun newPassengerPhone(offset: Int = 0): String =
            "+9477" + ((System.currentTimeMillis() / 1000 + offset) % 10_000_000)
                .toString()
                .padStart(7, '0')
    }
}
