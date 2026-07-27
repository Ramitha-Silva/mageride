package lk.mageride.shared.domain

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * The three C017 fences that cannot be proved at runtime, checked against the checked-in source.
 *
 * 1. **R-06's corrected geometry.** `GeoCellsTest` proves the view is 19 cells at res 7; what a
 *    runtime test cannot prove is that no *other* file in the geo/MQTT/SignalR surface went back
 *    to the superseded res-8 + ring(1) figure — a build that did would produce cell ids nothing
 *    publishes to and an empty map with no error anywhere.
 * 2. **The exact-distance post-filter is mandatory.** ADD §7.4 step 5: "The H3 cell alone is
 *    never treated as a final distance bound." The helper has to exist and has to be used.
 * 3. **AL-31 — the driver home map joins no geocell group.** Enforced by
 *    `LiveMapScope.DriverHomeMap` having no cells at all; the absence of a property is a statement
 *    about the source, and common code has no reflection to check it with.
 *
 * Like `ModelSourceHygieneTest` (C012), `PlatformSecurityHygieneTest` (C014) and
 * `MoneyDomainHygieneTest` (C016) this lives in `androidHostTest` — the only source set with a
 * filesystem. **Comments are stripped first**: these files' whole job is to *document* why res-8
 * is wrong, and a check that fired on the explanation would push the explanation out of the code.
 */
class GeoRealtimeHygieneTest {

    @Test
    fun no_file_in_the_realtime_surface_names_the_superseded_resolution() {
        val offenders = realtimeSources()
            .flatMap { file ->
                val code = codeOf(file.readText())
                SUPERSEDED_GEOMETRY.filter { it in code }.map { "${file.name}: $it" }
            }
            .toList()

        assertTrue(
            offenders.isEmpty(),
            "R-06 — the passenger view is res-7 + ring(2) = 19 cells, never res-8 + ring(1); found $offenders",
        )
    }

    @Test
    fun the_two_resolutions_are_declared_once_and_are_seven_and_five() {
        // The counter-check on the rule above: it must be passing because the right numbers are
        // there, not because the constants were quietly deleted.
        val code = codeOf(sourceFile(GEO_CELLS))

        assertTrue("VIEW_RESOLUTION: Int = 7" in code, "R-06 passenger view")
        assertTrue("DISPATCH_RESOLUTION: Int = 5" in code, "D5' §3.1 dispatch pre-filter")
        assertTrue("PASSENGER_VIEW_CELL_COUNT: Int = 19" in code)
    }

    @Test
    fun the_exact_distance_post_filter_exists_and_the_cell_helpers_point_at_it() {
        assertTrue(
            "fun <T> exactWithin" in codeOf(sourceFile(GEO_DISTANCE)),
            "the mandatory post-filter (R-06, D5' §3.1)",
        )
        // Deliberately checked against the *uncommented* file: what has to survive here is the
        // documentation on the cell helpers saying a cell is not a distance bound. A caller who
        // never reads it is the failure mode this fence is about.
        assertTrue("exactWithin" in sourceFile(GEO_CELLS), "the cell helpers must name what bounds them")
    }

    @Test
    fun the_driver_home_map_carries_no_cells_to_join() {
        val declaration = codeOf(sourceFile(LIVE_MAP_SCOPE))
            .substringAfter("data object DriverHomeMap")
            .substringBefore("}")

        assertTrue("emptySet()" in declaration, "AL-31 — the driver home map joins no geocell group")
        CELL_JOINING.forEach {
            assertTrue(it !in declaration, "AL-31 — found $it on the driver home map")
        }
    }

    @Test
    fun the_hub_contract_and_the_topic_tree_are_declared_once_each() {
        // Both surfaces resolve their names as strings at runtime; a second spelling anywhere is a
        // handler that is never invoked or a publish nothing consumes.
        val hubNames = sourcesUnder("realtime").filter { "\"JoinGeocells\"" in codeOf(it.readText()) }.toList()
        val liveTopic = sourcesUnder("mqtt").filter { "/pos/live\"" in codeOf(it.readText()) }.toList()

        assertEquals(listOf("LiveHub.kt"), hubNames.map { it.name }, "JoinGeocells is spelled once")
        assertEquals(listOf("MqttTopics.kt"), liveTopic.map { it.name }, "topics are built in one place")
    }

    // ------------------------------------------------------------------------------------------

    private fun realtimeSources(): Sequence<File> = REALTIME_PACKAGES.asSequence().flatMap { sourcesUnder(it) }

    private fun sourcesUnder(relativePackage: String): Sequence<File> {
        val dir = File(moduleDir(), "src/commonMain/kotlin/lk/mageride/shared/$relativePackage")
        if (!dir.isDirectory) fail("could not read $relativePackage")
        return dir.walkTopDown().filter { it.isFile && it.extension == "kt" }
    }

    private fun sourceFile(relative: String): String {
        val file = File(moduleDir(), relative)
        if (!file.isFile) fail("could not read $relative")
        return file.readText()
    }

    /**
     * The file with its comments blanked out.
     *
     * Same hand-rolled scanner as `MoneyDomainHygieneTest` (C016), for the same reason: the obvious
     * block-comment regex blows the stack on a file this size.
     */
    private fun codeOf(source: String): String {
        val code = StringBuilder(source.length)
        var index = 0
        var depth = 0
        while (index < source.length) {
            val rest = source.length - index
            when {
                rest >= 2 && source.startsWith("/*", index) -> {
                    depth++
                    index += 2
                }

                depth > 0 && rest >= 2 && source.startsWith("*/", index) -> {
                    depth--
                    index += 2
                }

                depth > 0 -> index++

                rest >= 2 && source.startsWith("//", index) -> {
                    while (index < source.length && source[index] != '\n') index++
                }

                else -> code.append(source[index++])
            }
        }
        return code.toString()
    }

    /** `shared/kmp`, whether the test runs from Gradle (module) or from an IDE (repository root). */
    private fun moduleDir(): File {
        val workingDir = requireNotNull(System.getProperty("user.dir")) { "user.dir is unset" }
        var dir: File? = File(workingDir).absoluteFile
        while (dir != null) {
            if (File(dir, MARKER).isFile) return dir
            val nested = File(dir, "shared/kmp")
            if (File(nested, MARKER).isFile) return nested
            dir = dir.parentFile
        }
        fail("could not locate the shared module from $workingDir")
    }

    private companion object {
        const val MARKER = "src/commonMain/kotlin/lk/mageride/shared/domain/geo/GeoCells.kt"
        const val GEO_CELLS = "src/commonMain/kotlin/lk/mageride/shared/domain/geo/GeoCells.kt"
        const val GEO_DISTANCE = "src/commonMain/kotlin/lk/mageride/shared/domain/geo/GeoDistance.kt"
        const val LIVE_MAP_SCOPE = "src/commonMain/kotlin/lk/mageride/shared/realtime/LiveMapScope.kt"

        /** Every package C017 owns. */
        val REALTIME_PACKAGES = listOf("domain/geo", "mqtt", "realtime")

        /**
         * Spellings the superseded R-06 geometry would have to take.
         *
         * Not the bare digit 8 — a resolution is always written next to what it resolves.
         */
        val SUPERSEDED_GEOMETRY = listOf(
            "RESOLUTION: Int = 8",
            "resolution = 8",
            "cellAt(point, 8)",
            "res8",
            "RES_8",
        )

        /** Any way the driver home map could end up in a public geocell group. */
        val CELL_JOINING = listOf("cellGroup", "cells", "H3Cell")
    }
}
