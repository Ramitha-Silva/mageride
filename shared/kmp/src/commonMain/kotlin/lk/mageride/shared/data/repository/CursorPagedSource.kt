package lk.mageride.shared.data.repository

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest

/**
 * A cursor-paginated read, as the app layer consumes it.
 *
 * Every paged endpoint in the platform is the same shape — `?cursor=&limit=` in, `{ items,
 * cursor, hasMore }` out (D3' §0) — and eighteen of them are in this module's scope. This is the
 * one abstraction over all of them, so a list screen depends on "a source of pages of `T`"
 * rather than on the client that happens to serve it:
 *
 * ```
 * val history = CursorPagedSource { page -> rideApi.listRideHistory(page) }
 * history.asFlow().collect { row -> … }
 * ```
 *
 * Deliberately **not** a per-service interface. A paged read has no domain content — C015–C018
 * own the repositories that do — and the typed clients in `data/api` are already the injectable
 * seam the app layer swaps in a test double for.
 */
public fun interface CursorPagedSource<T> {

    /** Loads one page. Throws a [lk.mageride.shared.data.api.MageRideError] like any other call. */
    public suspend fun load(request: PageRequest): Page<T>
}

/**
 * Walks the cursor to the end, emitting each row as it arrives.
 *
 * Stops when the server says `hasMore == false`, when it returns no cursor, or after
 * [maxPages] — a server that keeps handing back a cursor must not turn a list screen into an
 * unbounded loop.
 *
 * @param pageSize `?limit=`; `null` leaves the server default of [PageRequest.DEFAULT_LIMIT].
 * @param maxPages Safety stop.
 */
public fun <T> CursorPagedSource<T>.asFlow(pageSize: Int? = null, maxPages: Int = DEFAULT_MAX_PAGES): Flow<T> = flow {
    pages(pageSize, maxPages) { page -> page.items.forEach { emit(it) } }
}

/** As [asFlow], but emitting whole pages — for a screen that shows "page N of …" affordances. */
public fun <T> CursorPagedSource<T>.asPageFlow(
    pageSize: Int? = null,
    maxPages: Int = DEFAULT_MAX_PAGES,
): Flow<Page<T>> = flow {
    pages(pageSize, maxPages) { page -> emit(page) }
}

/**
 * Collects every page into one list.
 *
 * For a bounded read — a driver's saved addresses, one ride's payments. Do not use it for ride
 * history or a wallet ledger: those are unbounded and belong in [asFlow].
 */
public suspend fun <T> CursorPagedSource<T>.loadAll(
    pageSize: Int? = null,
    maxPages: Int = DEFAULT_MAX_PAGES,
): List<T> = buildList {
    var loaded = 0
    var cursor: String? = null
    while (loaded < maxPages) {
        val page = load(PageRequest(cursor = cursor, limit = pageSize))
        addAll(page.items)
        cursor = page.cursor
        loaded++
        if (!page.hasMore || cursor == null) break
    }
}

/** The next request after [page], or `null` when this was the last one. */
public fun PageRequest.next(page: Page<*>): PageRequest? =
    if (page.hasMore && page.cursor != null) copy(cursor = page.cursor) else null

/** Default safety stop: 100 pages of the maximum 100 rows is 10 000 rows. */
public const val DEFAULT_MAX_PAGES: Int = 100

private suspend inline fun <T> CursorPagedSource<T>.pages(pageSize: Int?, maxPages: Int, onPage: (Page<T>) -> Unit) {
    var cursor: String? = null
    var loaded = 0
    while (loaded < maxPages) {
        val page = load(PageRequest(cursor = cursor, limit = pageSize))
        onPage(page)
        cursor = page.cursor
        loaded++
        if (!page.hasMore || cursor == null) break
    }
}
