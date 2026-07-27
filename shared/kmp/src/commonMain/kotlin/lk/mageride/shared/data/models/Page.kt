package lk.mageride.shared.data.models

import kotlinx.serialization.Serializable

/**
 * The cursor-pagination envelope every list endpoint returns
 * (`_shared.yaml#/components/schemas/CursorPage`, D3' §0).
 *
 * Requests page with `?cursor=&limit=` (default 20, max 100 — see [PageRequest]); responses come
 * back as `{ items, cursor, hasMore }`.
 *
 * **`cursor` is `null` on the last page, never omitted.** The server force-serialises it for that
 * reason (C002 decision 9), so "last page" cannot be confused with "field missing". The client
 * side of that distinction is [hasMore], which is why it is a separate required field rather than
 * something derived from a null cursor.
 *
 * On the wire this is `allOf(CursorPage, { items: [T] })` — one generic type covers every such
 * response, so C013 has one pagination helper rather than forty near-identical envelopes.
 *
 * @property items This page's rows, in the order the endpoint documents (newest first, almost
 *   everywhere).
 * @property cursor Opaque continuation token; pass it back as `?cursor=` for the next page.
 *   `null` when there is no next page.
 * @property hasMore Whether another page exists.
 */
@Serializable
public data class Page<T>(val items: List<T> = emptyList(), val cursor: String? = null, val hasMore: Boolean = false) {
    /** True when this page carries no rows at all. */
    public val isEmpty: Boolean get() = items.isEmpty()

    /** Maps the rows, keeping the cursor and the has-more flag intact. */
    public fun <R> map(transform: (T) -> R): Page<R> =
        Page(items = items.map(transform), cursor = cursor, hasMore = hasMore)

    public companion object {
        /** The last (and only) page of a list that fits in one response — useful in tests. */
        public fun <T> of(items: List<T>): Page<T> = Page(items = items)
    }
}

/**
 * The `?cursor=&limit=` query pair (`_shared.yaml#/components/parameters/{Cursor,Limit}`).
 *
 * Not a body — it is modelled here so C013's clients take one argument per paged call instead of
 * two loose nullables, and so the platform's page-size bounds live in one place.
 *
 * @property cursor `null` for the first page; otherwise the previous [Page.cursor].
 * @property limit Page size. `null` leaves the server's default of [DEFAULT_LIMIT].
 */
public data class PageRequest(val cursor: String? = null, val limit: Int? = null) {
    init {
        require(limit == null || limit in MIN_LIMIT..MAX_LIMIT) { LIMIT_OUT_OF_RANGE }
    }

    public companion object {
        /** Server-side default page size when `?limit=` is omitted (D3' §0). */
        public const val DEFAULT_LIMIT: Int = 20

        /** Smallest page the gateway accepts. */
        public const val MIN_LIMIT: Int = 1

        /** Largest page the gateway accepts; anything above is `400 validation-failed`. */
        public const val MAX_LIMIT: Int = 100

        private const val LIMIT_OUT_OF_RANGE: String = "limit must be between 1 and 100"

        /** The first page, at the server's default size. */
        public val FIRST: PageRequest = PageRequest()
    }
}
