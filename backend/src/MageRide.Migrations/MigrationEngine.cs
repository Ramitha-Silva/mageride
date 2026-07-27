using System.Reflection;
using System.Text;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Helpers;
using Npgsql;

namespace MageRide.Migrations;

/// <summary>
/// The one place the DbUp pipeline is configured (C003). The <c>migrate</c> container's
/// entry point and the integration-test harness (<c>MageRide.TestKit</c>, C010) both call
/// in here, so a test database and a deployed database are migrated by identical code.
/// </summary>
/// <remarks>
/// Extracted from <c>Program.cs</c> by C010. Nothing about the pipeline changed: the journal
/// is still <c>public.schema_versions</c>, scripts still run one per transaction in filename
/// order, and <c>--ignore-journal</c> still swaps in a null journal so every script re-executes.
/// </remarks>
public static class MigrationEngine
{
    /// <summary>Schema holding the DbUp journal.</summary>
    public const string JournalSchema = "public";

    /// <summary>Table holding the DbUp journal. A migrated database is identified by this table.</summary>
    public const string JournalTable = "schema_versions";

    /// <summary>Per-script command timeout when the caller does not supply one.</summary>
    public static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(300);

    private const string ResourcePrefix = "MageRide.Migrations.Scripts.";

    /// <summary>
    /// Loads the migration scripts, from <paramref name="directory"/> when given and from the
    /// assembly's embedded set otherwise.
    /// </summary>
    /// <remarks>
    /// Both sources name a script by its bare filename. DbUp keys the journal on that name, so
    /// normalising it means a database migrated from a directory and one migrated from the
    /// embedded set agree about what has already run — otherwise switching source would
    /// re-apply everything.
    /// </remarks>
    public static IReadOnlyList<SqlScript> LoadScripts(string? directory = null) =>
        directory is null ? FromEmbedded() : FromDirectory(directory);

    /// <summary>Configures a DbUp upgrader without running it.</summary>
    public static UpgradeEngine Build(
        string connectionString,
        IReadOnlyList<SqlScript> scripts,
        TimeSpan? executionTimeout = null,
        bool ignoreJournal = false,
        IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);

        var builder = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScripts(scripts)
            .WithExecutionTimeout(executionTimeout ?? DefaultExecutionTimeout)
            .WithTransactionPerScript();

        if (log is not null)
        {
            builder = builder.LogTo(log);
        }

        // The journal is what makes a re-run a no-op. Ignoring it drops the journal so every
        // script runs again: that is how migrate-verify.sh proves the DDL itself is idempotent,
        // rather than only proving that DbUp remembers what it already did.
        builder = ignoreJournal
            ? builder.JournalTo(new NullJournal())
            : builder.JournalToPostgresqlTable(JournalSchema, JournalTable);

        return builder.Build();
    }

    /// <summary>Applies every pending script and reports what happened.</summary>
    public static MigrationOutcome Apply(
        string connectionString,
        string? scriptsDirectory = null,
        bool ignoreJournal = false,
        TimeSpan? executionTimeout = null,
        IUpgradeLog? log = null)
    {
        var scripts = LoadScripts(scriptsDirectory);

        if (scripts.Count == 0)
        {
            return new MigrationOutcome(
                Successful: false,
                ScriptsApplied: 0,
                AvailableScripts: 0,
                FailedScript: null,
                Error: new InvalidOperationException(
                    "no migration scripts found. Expected db/migrations/*.sql embedded in the "
                    + "assembly, or a directory in --scripts / MIGRATE_SCRIPTS_DIR."));
        }

        var result = Build(connectionString, scripts, executionTimeout, ignoreJournal, log).PerformUpgrade();

        return new MigrationOutcome(
            Successful: result.Successful,
            ScriptsApplied: result.Scripts.Count(),
            AvailableScripts: scripts.Count,
            FailedScript: result.ErrorScript?.Name,
            Error: result.Error);
    }

    /// <summary>
    /// Polls until PostgreSQL accepts a connection, or the timeout elapses.
    /// </summary>
    /// <remarks>
    /// The compose <c>migrate</c> service starts alongside Postgres rather than strictly after
    /// it, and a fresh container spends a few seconds in initdb. Polling turns a startup race
    /// into a wait instead of a crash-loop.
    /// </remarks>
    public static async Task<DatabaseAvailability> WaitForDatabaseAsync(
        string connectionString,
        TimeSpan timeout,
        IUpgradeLog? log = null,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return new DatabaseAvailability(Reachable: true, LastError: null);
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        var announced = false;
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand("SELECT 1;", connection);
                await command.ExecuteScalarAsync(cancellationToken);
                return new DatabaseAvailability(Reachable: true, LastError: null);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                last = ex;

                if (!announced)
                {
                    log?.LogInformation(
                        "Waiting up to {0}s for PostgreSQL to accept connections...",
                        (int)timeout.TotalSeconds);
                    announced = true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        return new DatabaseAvailability(Reachable: false, LastError: last);
    }

    private static IReadOnlyList<SqlScript> FromEmbedded()
    {
        var assembly = typeof(MigrationEngine).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => new SqlScript(name[ResourcePrefix.Length..], ReadResource(assembly, name)))
            .OrderBy(script => script.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<SqlScript> FromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Migration script directory '{directory}' does not exist.");
        }

        return Directory.EnumerateFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .Select(path => new SqlScript(Path.GetFileName(path), File.ReadAllText(path, Encoding.UTF8)))
            .OrderBy(script => script.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded migration '{name}' could not be opened.");

        // The seed data carries Sinhala and Tamil city names, so the encoding is not optional.
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>Result of <see cref="MigrationEngine.WaitForDatabaseAsync"/>.</summary>
/// <param name="Reachable">True once the database answered <c>SELECT 1</c>.</param>
/// <param name="LastError">
/// The final connection failure when it never answered — reported so the operator sees *why*
/// the database was unreachable rather than only that it was.
/// </param>
public sealed record DatabaseAvailability(bool Reachable, Exception? LastError);

/// <summary>What one <see cref="MigrationEngine.Apply"/> call did.</summary>
/// <param name="Successful">False when a script errored; <paramref name="Error"/> says why.</param>
/// <param name="ScriptsApplied">Scripts executed by this call. Zero on a journalled re-run.</param>
/// <param name="AvailableScripts">Scripts the source offered, applied or not.</param>
/// <param name="FailedScript">Name of the script that errored, when one did.</param>
/// <param name="Error">The failure, when there was one.</param>
public sealed record MigrationOutcome(
    bool Successful,
    int ScriptsApplied,
    int AvailableScripts,
    string? FailedScript,
    Exception? Error);
