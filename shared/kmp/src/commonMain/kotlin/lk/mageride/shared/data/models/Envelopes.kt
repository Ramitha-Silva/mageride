package lk.mageride.shared.data.models

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * The acknowledgement every HMAC-signed payment-provider callback answers with
 * (fare-svc, wallet-svc and subscription-svc all declare the identical `{ received: true }`).
 *
 * The same body is returned for a **redelivery**: the callbacks are the platform's only
 * `Idempotency-Key` exemptions and dedupe on `provider_transaction_id` instead (R-19), because an
 * external gateway will not send our header.
 *
 * Modelled here rather than three times over because the shape is one shape. A mobile client
 * never posts these — they are contracted so C013 and the C118 contract tests have a type.
 *
 * @property received Always `true`; the contract declares it `const`.
 */
@Serializable
public data class CallbackAck(val received: Boolean = true)

/**
 * The status a payment provider reports on a callback.
 *
 * Shared by `fare.yaml#ProviderCallback`, `wallet.yaml#TopupCallback` and
 * `subscription.yaml#SubscriptionProviderCallback`, which declare it identically.
 */
@Serializable
public enum class ProviderCallbackStatus {
    SUCCESS,
    FAILED,
    PENDING,
}

/**
 * Whether a request to see something — a Mode B vehicle, a shared vehicle — has been decided.
 *
 * Matches the `subscription.access_requests.status` and `registry.share_requests.status` CHECKs,
 * which carry the same three values (C003, C005).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class AccessRequestStatus(public val wire: String) {
    @SerialName("pending")
    PENDING("pending"),

    @SerialName("accepted")
    ACCEPTED("accepted"),

    @SerialName("rejected")
    REJECTED("rejected"),
}
