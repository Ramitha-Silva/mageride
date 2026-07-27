package lk.mageride.shared.data.api

/**
 * The backend services this module can call — one entry per file in `backend/contracts/`
 * that the four apps are allowed to reach (C012 decision 1).
 *
 * This is not decoration. Every request carries its service, and the resilience layer keys its
 * circuit breaker on it (D6' §8.3 "per external dependency"): a fare-svc outage must not open
 * the breaker in front of ride-svc, even though both are behind the same gateway host.
 *
 * @property id The service's name as it appears in `backend/contracts/{id}.yaml`.
 */
public enum class ApiService(public val id: String) {
    IAM("iam"),
    REGISTRY("registry"),
    TRIP_STATE("trip-state"),
    RIDE("ride"),
    DISPATCH("dispatch"),
    FARE("fare"),
    SUBSCRIPTION("subscription"),
    WALLET("wallet"),
    QUERY("query"),
    TRANSIT("transit"),
    SAFETY("safety"),
    SUPPORT("support"),
    CONTENT("content"),
    VOIP("voip"),
    NOTIFICATION("notification"),
    VERSION("version-check"),
}
