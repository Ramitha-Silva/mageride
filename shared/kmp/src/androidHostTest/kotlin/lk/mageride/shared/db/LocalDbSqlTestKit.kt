package lk.mageride.shared.db

import app.cash.sqldelight.db.QueryResult
import app.cash.sqldelight.db.SqlDriver
import app.cash.sqldelight.db.SqlSchema
import app.cash.sqldelight.driver.jdbc.sqlite.JdbcSqliteDriver
import lk.mageride.shared.db.driver.DriverDb
import lk.mageride.shared.db.passenger.PassengerDb
import java.io.File
import kotlin.time.Instant

// Everything in this source set runs against a REAL SQLite (xerial, through SQLDelight's JDBC
// driver) rather than a fake. That matters: the fences C018 has to hold — a CHECK constraint, a
// composite primary key, a migration that rebuilds a table, `PRAGMA user_version` — are properties
// of the engine, not of Kotlin. commonTest covers the rules; this covers the SQL.
//
// androidHostTest rather than commonTest because there is no SQLite driver for Kotlin/Native on a
// Linux build host; the iOS half of this module is type-checked here and tested on macOS.

internal val NOW: Instant = Instant.parse("2026-07-27T06:00:00Z")

/** Opens (creating or migrating) a database at [url] and stamps `PRAGMA user_version`. */
internal fun openDriverAt(url: String, schema: SqlSchema<QueryResult.Value<Unit>>): SqlDriver {
    val driver = JdbcSqliteDriver(url)
    when (val current = driver.userVersion()) {
        0L -> schema.create(driver)
        schema.version -> Unit
        else -> schema.migrate(driver, current, schema.version)
    }
    driver.setUserVersion(schema.version)
    return driver
}

/** A throwaway in-memory passenger database at the current schema version. */
internal fun openPassenger(): PassengerDb = PassengerDb(openDriverAt(JdbcSqliteDriver.IN_MEMORY, PassengerDb.SCHEMA))

/** A throwaway in-memory driver database at the current schema version. */
internal fun openDriverDb(): DriverDb = DriverDb(openDriverAt(JdbcSqliteDriver.IN_MEMORY, DriverDb.SCHEMA))

/** A file-backed driver database — the only way to prove something survives a close and reopen. */
internal fun openDriverDb(file: File): DriverDb =
    DriverDb(openDriverAt("jdbc:sqlite:${file.absolutePath}", DriverDb.SCHEMA))

/** A temp file that does not exist yet, so the first open creates the schema. */
internal fun tempDbFile(prefix: String): File =
    File.createTempFile(prefix, ".db").also { check(it.delete()) { "could not clear $it" } }

// ---- schema introspection -----------------------------------------------------------------

/** One row of `PRAGMA table_info`. */
internal data class ColumnInfo(
    val name: String,
    val type: String,
    val notNull: Boolean,
    val defaultValue: String?,
    val primaryKey: Int,
)

internal fun SqlDriver.userVersion(): Long = queryOne("PRAGMA user_version") { it.getLong(0) ?: 0L }

internal fun SqlDriver.setUserVersion(version: Long) {
    execute(identifier = null, sql = "PRAGMA user_version = $version", parameters = 0)
}

internal fun SqlDriver.tableNames(): List<String> = userTables()

internal fun SqlDriver.columnsOf(table: String): List<ColumnInfo> = queryList("PRAGMA table_info($table)") {
    ColumnInfo(
        name = it.getString(1).orEmpty(),
        type = it.getString(2).orEmpty(),
        notNull = (it.getLong(3) ?: 0L) != 0L,
        defaultValue = it.getString(4),
        primaryKey = (it.getLong(5) ?: 0L).toInt(),
    )
}

internal fun SqlDriver.columnNamesOf(table: String): Set<String> = columnsOf(table).map { it.name }.toSet()

/** Index name to its columns, in index order. Auto-indexes (`sqlite_autoindex_*`) are excluded. */
internal fun SqlDriver.indexesOf(table: String): Map<String, List<String>> =
    queryList("PRAGMA index_list($table)") { it.getString(1).orEmpty() }
        .filterNot { it.startsWith("sqlite_autoindex") }
        .associateWith { index -> queryList("PRAGMA index_info($index)") { it.getString(2).orEmpty() } }

/**
 * The `CREATE TABLE` text SQLite stored, normalised to structure only.
 *
 * SQLite keeps the statement VERBATIM, comments and all, so the `.sq` files' inline column notes
 * come back with it while the `.sqm` rebuilds — which carry no comments — do not. Line comments
 * are stripped before whitespace is collapsed; double quotes go too, because
 * `ALTER TABLE x RENAME TO y` rewrites the stored name as `"y"`.
 */
internal fun SqlDriver.createSqlOf(table: String): String =
    queryOne("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '$table'") {
        it.getString(0).orEmpty()
    }
        .lines().joinToString(" ") { it.substringBefore("--") }
        .replace("\"", "")
        .replace(Regex("\\s+"), " ")
        .trim()

private fun <T : Any> SqlDriver.queryOne(sql: String, row: (app.cash.sqldelight.db.SqlCursor) -> T): T = executeQuery(
    identifier = null,
    sql = sql,
    mapper = { cursor ->
        check(cursor.next().value) { "no row for: $sql" }
        QueryResult.Value(row(cursor))
    },
    parameters = 0,
).value

private fun <T : Any> SqlDriver.queryList(sql: String, row: (app.cash.sqldelight.db.SqlCursor) -> T): List<T> =
    executeQuery(
        identifier = null,
        sql = sql,
        mapper = { cursor ->
            val out = mutableListOf<T>()
            while (cursor.next().value) out += row(cursor)
            QueryResult.Value(out.toList())
        },
        parameters = 0,
    ).value

/** Runs [sql] as a script — the migration fixtures are multi-statement. */
internal fun SqlDriver.executeScript(sql: String) {
    sql.split(";")
        .map { it.trim() }
        .filter { it.isNotEmpty() && !it.lines().all { line -> line.trim().startsWith("--") } }
        .forEach { execute(identifier = null, sql = it, parameters = 0) }
}
