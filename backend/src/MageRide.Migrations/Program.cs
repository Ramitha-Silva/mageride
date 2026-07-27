using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Helpers;
using Npgsql;

// =========================================================================================
// MageRide migration runner (C003).
//
// Applies the versioned .sql scripts in db/migrations to a PostgreSQL 16 database, in
// filename order, recording each in public.schema_versions. Ships as the one-shot `migrate`
// container that runs ahead of app-services (D7' §3).
//
// Schema changes are SQL scripts, never `dotnet ef` (AL-53, D7' §1). This process is the
// only thing that writes DDL.
// =========================================================================================

var options = MigrationOptions.Parse(args);

if (options.ShowHelp)
{
    MigrationOptions.WriteUsage(Console.Out);
    return ExitCodes.Success;
}

if (options.UsageError is { } usageError)
{
    Console.Error.WriteLine($"error: {usageError}");
    Console.Error.WriteLine();
    MigrationOptions.WriteUsage(Console.Error);
    return ExitCodes.UsageError;
}

var log = new ConsoleUpgradeLog();

IReadOnlyList<SqlScript> scripts;
try
{
    scripts = ScriptSource.Load(options.ScriptsDirectory);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: could not load migration scripts: {ex.Message}");
    return ExitCodes.Failure;
}

if (scripts.Count == 0)
{
    Console.Error.WriteLine("error: no migration scripts found. Expected db/migrations/*.sql "
        + "embedded in the assembly, or a directory in --scripts / MIGRATE_SCRIPTS_DIR.");
    return ExitCodes.Failure;
}

log.LogInformation("MageRide migrations: {0} script(s) from {1}",
    scripts.Count, options.ScriptsDirectory is null ? "the embedded set" : options.ScriptsDirectory);

if (!await WaitForDatabaseAsync(options, log))
{
    return ExitCodes.Failure;
}

var builder = DeployChanges.To
    .PostgresqlDatabase(options.ConnectionString)
    .WithScripts(scripts)
    .WithExecutionTimeout(TimeSpan.FromSeconds(options.CommandTimeoutSeconds))
    .WithTransactionPerScript()
    .LogTo(log);

// The journal is what makes a re-run a no-op. --ignore-journal drops it so every script runs
// again: that is how the verify script proves the DDL itself is idempotent, rather than only
// proving that DbUp remembers what it already did.
builder = options.IgnoreJournal
    ? builder.JournalTo(new NullJournal())
    : builder.JournalToPostgresqlTable(JournalSchema, JournalTable);

var upgrader = builder.Build();

if (options.WhatIf)
{
    var pending = upgrader.GetScriptsToExecute();
    if (pending.Count == 0)
    {
        log.LogInformation("No scripts to execute — the database is up to date.");
        return ExitCodes.Success;
    }

    log.LogInformation("{0} script(s) pending:", pending.Count);
    foreach (var script in pending)
    {
        log.LogInformation("  {0}", script.Name);
    }

    return ExitCodes.Success;
}

var stopwatch = Stopwatch.StartNew();
var result = upgrader.PerformUpgrade();
stopwatch.Stop();

if (!result.Successful)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"error: migration failed on '{result.ErrorScript?.Name ?? "<unknown>"}': {result.Error?.Message}");
    return ExitCodes.Failure;
}

log.LogInformation("Applied {0} script(s) in {1:0.0}s. Database is up to date.",
    result.Scripts.Count(), stopwatch.Elapsed.TotalSeconds);

return ExitCodes.Success;

// -----------------------------------------------------------------------------------------

static async Task<bool> WaitForDatabaseAsync(MigrationOptions options, IUpgradeLog log)
{
    if (options.WaitSeconds <= 0)
    {
        return true;
    }

    // The compose `migrate` service starts alongside Postgres rather than strictly after it,
    // and a fresh container spends a few seconds in initdb. Polling here turns a startup race
    // into a wait instead of a crash-loop.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(options.WaitSeconds);
    Exception? last = null;
    var announced = false;

    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1;", connection);
            await command.ExecuteScalarAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            last = ex;

            if (!announced)
            {
                log.LogInformation("Waiting up to {0}s for PostgreSQL to accept connections...", options.WaitSeconds);
                announced = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    Console.Error.WriteLine($"error: PostgreSQL was not reachable within {options.WaitSeconds}s: {last?.Message}");
    return false;
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageError = 2;
}

public partial class Program
{
    internal const string JournalSchema = "public";
    internal const string JournalTable = "schema_versions";
}

/// <summary>
/// Loads the migration scripts, either from the embedded set or from a directory.
/// </summary>
/// <remarks>
/// Both sources name a script by its bare filename. DbUp keys the journal on that name, so
/// normalising it means a database migrated from a directory and one migrated from the
/// embedded set agree about what has already run — otherwise switching source would re-apply
/// everything.
/// </remarks>
internal static class ScriptSource
{
    private const string ResourcePrefix = "MageRide.Migrations.Scripts.";

    public static IReadOnlyList<SqlScript> Load(string? directory) =>
        directory is null ? FromEmbedded() : FromDirectory(directory);

    private static IReadOnlyList<SqlScript> FromEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();

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

/// <summary>Command-line and environment configuration for the runner.</summary>
internal sealed record MigrationOptions
{
    public string ConnectionString { get; private init; } = string.Empty;

    /// <summary><see langword="null"/> to use the embedded scripts.</summary>
    public string? ScriptsDirectory { get; private init; }

    public bool IgnoreJournal { get; private init; }

    public bool WhatIf { get; private init; }

    public int CommandTimeoutSeconds { get; private init; } = 300;

    public int WaitSeconds { get; private init; } = 60;

    public bool ShowHelp { get; private init; }

    public string? UsageError { get; private init; }

    public static MigrationOptions Parse(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? Environment.GetEnvironmentVariable("MIGRATE_CONNECTION");
        var scripts = Environment.GetEnvironmentVariable("MIGRATE_SCRIPTS_DIR");
        var ignoreJournal = IsTruthy(Environment.GetEnvironmentVariable("MIGRATE_IGNORE_JOURNAL"));
        var timeout = ParseInt(Environment.GetEnvironmentVariable("MIGRATE_TIMEOUT_SECONDS"), 300);
        var wait = ParseInt(Environment.GetEnvironmentVariable("MIGRATE_WAIT_SECONDS"), 60);
        var whatIf = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    return new MigrationOptions { ShowHelp = true };

                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;

                case "--scripts" when i + 1 < args.Length:
                    scripts = args[++i];
                    break;

                case "--timeout" when i + 1 < args.Length:
                    timeout = ParseInt(args[++i], timeout);
                    break;

                case "--wait" when i + 1 < args.Length:
                    wait = ParseInt(args[++i], wait);
                    break;

                case "--ignore-journal":
                    ignoreJournal = true;
                    break;

                case "--what-if":
                    whatIf = true;
                    break;

                default:
                    return new MigrationOptions { UsageError = $"unrecognised argument '{args[i]}'" };
            }
        }

        if (string.IsNullOrWhiteSpace(connection))
        {
            return new MigrationOptions
            {
                UsageError = "no connection string. Pass --connection or set ConnectionStrings__Postgres.",
            };
        }

        return new MigrationOptions
        {
            ConnectionString = connection,
            ScriptsDirectory = string.IsNullOrWhiteSpace(scripts) ? null : scripts,
            IgnoreJournal = ignoreJournal,
            WhatIf = whatIf,
            CommandTimeoutSeconds = timeout,
            WaitSeconds = wait,
        };
    }

    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("MageRide migration runner — applies db/migrations/*.sql via DbUp.");
        writer.WriteLine();
        writer.WriteLine("Usage: MageRide.Migrations [options]");
        writer.WriteLine();
        writer.WriteLine("  --connection <dsn>   Postgres connection string.");
        writer.WriteLine("                       Default: $ConnectionStrings__Postgres or $MIGRATE_CONNECTION.");
        writer.WriteLine("  --scripts <dir>      Read scripts from a directory instead of the embedded set.");
        writer.WriteLine("                       Default: $MIGRATE_SCRIPTS_DIR.");
        writer.WriteLine("  --ignore-journal     Re-run every script, ignoring public.schema_versions.");
        writer.WriteLine("                       Proves the scripts are idempotent; used by migrate-verify.sh.");
        writer.WriteLine("  --what-if            List the pending scripts and exit without applying them.");
        writer.WriteLine("  --timeout <seconds>  Per-script command timeout. Default 300 ($MIGRATE_TIMEOUT_SECONDS).");
        writer.WriteLine("  --wait <seconds>     Wait for Postgres to accept connections. Default 60,");
        writer.WriteLine("                       0 to fail fast ($MIGRATE_WAIT_SECONDS).");
        writer.WriteLine("  -h, --help           Show this help.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success · 1 migration failure · 2 bad usage.");
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
