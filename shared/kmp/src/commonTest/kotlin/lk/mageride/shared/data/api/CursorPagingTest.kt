package lk.mageride.shared.data.api

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.repository.CursorPagedSource
import lk.mageride.shared.data.repository.asFlow
import lk.mageride.shared.data.repository.asPageFlow
import lk.mageride.shared.data.repository.loadAll
import lk.mageride.shared.data.repository.next
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * The cursor-pagination helper (D3' §0): `?cursor=&limit=` in, `{ items, cursor, hasMore }` out.
 *
 * One helper for all eighteen paged reads in the module, so a list screen never re-implements the
 * "am I done?" rule — and never gets it subtly wrong for one endpoint.
 */
class CursorPagingTest {

    @Test
    fun the_flow_walks_every_page_in_order() = runTest {
        val source = fakePages(listOf(listOf("a", "b"), listOf("c", "d"), listOf("e")))

        assertEquals(listOf("a", "b", "c", "d", "e"), source.asFlow().toList())
    }

    @Test
    fun the_first_request_carries_no_cursor_and_later_ones_carry_the_previous_one() = runTest {
        val seen = mutableListOf<PageRequest>()
        val source = fakePages(listOf(listOf("a"), listOf("b")), seen)

        source.asFlow(pageSize = 50).toList()

        assertEquals(2, seen.size)
        assertNull(seen[0].cursor)
        assertEquals(50, seen[0].limit)
        assertEquals("cursor-1", seen[1].cursor)
    }

    @Test
    fun walking_stops_when_the_server_says_there_is_no_more() = runTest {
        var calls = 0
        val source = CursorPagedSource<String> {
            calls++
            Page(items = listOf("only"), cursor = "still-here", hasMore = false)
        }

        assertEquals(listOf("only"), source.asFlow().toList())
        assertEquals(1, calls, "hasMore=false ends it even when a cursor came back")
    }

    @Test
    fun walking_stops_when_the_cursor_is_null() = runTest {
        var calls = 0
        val source = CursorPagedSource<String> {
            calls++
            Page(items = listOf("only"), cursor = null, hasMore = true)
        }

        source.asFlow().toList()

        assertEquals(1, calls, "hasMore=true with no cursor would otherwise loop forever")
    }

    @Test
    fun a_runaway_server_is_bounded_by_max_pages() = runTest {
        var calls = 0
        val source = CursorPagedSource<String> {
            calls++
            Page(items = listOf("row-$calls"), cursor = "always", hasMore = true)
        }

        val rows = source.asFlow(maxPages = 4).toList()

        assertEquals(4, rows.size)
        assertEquals(4, calls)
    }

    @Test
    fun the_page_flow_emits_whole_pages() = runTest {
        val source = fakePages(listOf(listOf("a", "b"), listOf("c")))

        val pages = source.asPageFlow().toList()

        assertEquals(2, pages.size)
        assertEquals(listOf("a", "b"), pages[0].items)
        assertEquals(listOf("c"), pages[1].items)
    }

    @Test
    fun load_all_collects_a_bounded_read_into_one_list() = runTest {
        val source = fakePages(listOf(listOf("a"), listOf("b"), listOf("c")))

        assertEquals(listOf("a", "b", "c"), source.loadAll())
    }

    @Test
    fun next_returns_null_on_the_last_page() {
        val request = PageRequest(limit = 20)

        assertEquals("c2", request.next(Page(items = listOf(1), cursor = "c2", hasMore = true))?.cursor)
        assertNull(request.next(Page(items = listOf(1), cursor = null, hasMore = false)))
        assertNull(request.next(Page(items = listOf(1), cursor = "c2", hasMore = false)))
    }

    @Test
    fun the_page_size_bounds_come_from_the_contract() {
        assertEquals(DEFAULT_LIMIT, PageRequest.DEFAULT_LIMIT)
        assertEquals(MIN_LIMIT, PageRequest.MIN_LIMIT)
        assertEquals(MAX_LIMIT, PageRequest.MAX_LIMIT)
    }

    @Test
    fun a_typed_client_read_can_be_wrapped_as_a_paged_source() = runTest {
        // The idiom the app layer uses: the client stays the seam, the helper does the walking.
        val test = testApi { attempt, _ ->
            if (attempt == 0) {
                respondJson(
                    """{"items":[{"rideId":"01R","state":"Completed","completedAt":"2026-07-27T04:15:00Z"}],
                       "cursor":"p2","hasMore":true}
                    """.trimIndent(),
                )
            } else {
                respondJson(
                    """{"items":[{"rideId":"02R","state":"Completed","completedAt":"2026-07-27T05:15:00Z"}],
                       "cursor":null,"hasMore":false}
                    """.trimIndent(),
                )
            }
        }
        val history = CursorPagedSource { page -> test.api.ride.listRideHistory(page) }

        val rows = history.asFlow().toList()

        assertEquals(listOf("01R", "02R"), rows.map { it.rideId })
        assertEquals("p2", test.requests[1].query["cursor"])
    }

    private fun fakePages(
        pages: List<List<String>>,
        seen: MutableList<PageRequest> = mutableListOf(),
    ): CursorPagedSource<String> {
        var index = 0
        return CursorPagedSource { request ->
            seen += request
            val items = pages[index]
            val last = index == pages.lastIndex
            index++
            Page(items = items, cursor = if (last) null else "cursor-$index", hasMore = !last)
        }
    }

    private companion object {
        const val DEFAULT_LIMIT = 20
        const val MIN_LIMIT = 1
        const val MAX_LIMIT = 100
    }
}
