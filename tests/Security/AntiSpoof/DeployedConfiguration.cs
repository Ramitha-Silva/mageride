using Microsoft.Extensions.Configuration;

namespace MageRide.Security.Tests.AntiSpoof;

/// <summary>
/// The anti-spoof thresholds <b>as deployed</b>, read out of <c>infra/env/.env.app.example</c>.
/// </summary>
/// <remarks>
/// <para>
/// C128's first fence is that thresholds are per vehicle type and <i>configurable</i> — "tuning
/// means changing config plus tests, not hardcoding". A corpus measured against
/// <c>new PositionProcessorOptions()</c> would measure the class's initialisers, which is the one
/// place tuning is not supposed to happen; a fleet retuned in the environment file and left
/// unmeasured is exactly the outcome the fence exists to prevent.
/// </para>
/// <para>
/// So the measurement binds the environment file, the same double-underscore keys the compose
/// files and the replica load, through the same <c>IConfiguration</c> shape the service binds.
/// <c>ThresholdConfigurationTests</c> then asserts the two agree, so a divergence is reported as a
/// divergence rather than silently deciding which of them the corpus was about.
/// </para>
/// <para>
/// <b>The example file, not a real one.</b> <c>infra/CLAUDE.md</c>: "the `.example` files are the
/// default config layer, loaded first by every service". `.env.common` and `.env.app` are
/// gitignored and absent by default, so the committed example is both the platform default and the
/// only layer a build agent can see.
/// </para>
/// </remarks>
internal static class DeployedConfiguration
{
    private static readonly Lazy<IConfigurationRoot> Instance = new(Load);

    /// <summary>Everything <c>.env.app.example</c> sets, as configuration keys.</summary>
    public static IConfiguration Current => Instance.Value;

    /// <summary>The repository root — the directory holding <c>backend/</c> and <c>infra/</c>.</summary>
    public static string RepositoryRoot { get; } = LocateRoot();

    /// <summary>Binds one section of the deployed configuration onto a fresh options instance.</summary>
    public static T Bind<T>(string section)
        where T : new()
    {
        var options = new T();
        Current.GetSection(section).Bind(options);

        return options;
    }

    private static IConfigurationRoot Load()
    {
        var path = Path.Combine(RepositoryRoot, "infra", "env", ".env.app.example");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The deployed application environment file is missing: {path}. The anti-spoof "
                + "thresholds are read from it rather than from a C# initialiser (C128 fence 1).",
                path);
        }

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var split = line.IndexOf('=', StringComparison.Ordinal);

            if (split <= 0)
            {
                continue;
            }

            // `Section__Key` is the environment spelling of `Section:Key`; a dictionary keyed on
            // the double underscore binds to nothing at all, silently.
            var key = line[..split].Replace("__", ":", StringComparison.Ordinal);
            var value = line[(split + 1)..];

            // Compose interpolates `$$` to a literal `$` inside the container (infra/CLAUDE.md).
            settings[key] = value.Replace("$$", "$", StringComparison.Ordinal);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static string LocateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "infra", "env"))
                && Directory.Exists(Path.Combine(directory.FullName, "backend", "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"The repository root was not found above {AppContext.BaseDirectory}.");
    }
}
