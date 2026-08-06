package lk.mageride.shared.data.api

/**
 * The result of a conditional `GET` — the value, or "you already have it".
 *
 * Exactly one operation needs this: `GET /v1/config/cities` is the only contract route that
 * declares an `ETag`, a `Cache-Control` and a `304` (`content.yaml`). It is also the route every
 * app hits on first run and on every cold start, so revalidating instead of refetching is the
 * difference between a 6 kB response and an empty one on a 2G connection.
 */
public sealed interface Conditional<out T> {

    /** The server sent a fresh representation. */
    public data class Value<out T>(val value: T, val etag: String?) : Conditional<T>

    /** `304` — the cached copy the caller quoted is still current. */
    public data object NotModified : Conditional<Nothing>

    /** The value if the server sent one, or `null` on a `304`. */
    public val valueOrNull: T?
        get() = when (this) {
            is Value -> value
            NotModified -> null
        }

    /**
     * The `ETag` to quote on the next request, or `null` on a `304` (keep the one you have).
     *
     * The sibling of [valueOrNull], and it exists for the same reason: a caller that only wants
     * "the body, if there is one, and the tag that came with it" should not have to smart-cast to
     * [Value] to get the second half. **Swift in particular cannot** — Kotlin/Native exports a
     * generic interface with its type parameter erased, so a `Conditional` reaching an iOS app is
     * an unparameterised existential and `as? Conditional.Value<T>` has no spelling. The two
     * properties together are the whole of what an app needs, and neither casts.
     */
    public val etagOrNull: String?
        get() = when (this) {
            is Value -> etag
            NotModified -> null
        }
}
