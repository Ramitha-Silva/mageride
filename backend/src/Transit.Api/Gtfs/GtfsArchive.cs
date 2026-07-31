using System.IO.Compression;

namespace MageRide.Transit.Gtfs;

/// <summary>The GTFS files BR-32.1 names, required and optional.</summary>
internal static class GtfsFiles
{
    public const string Agency = "agency.txt";
    public const string Routes = "routes.txt";
    public const string Trips = "trips.txt";
    public const string Stops = "stops.txt";
    public const string StopTimes = "stop_times.txt";
    public const string Calendar = "calendar.txt";
    public const string CalendarDates = "calendar_dates.txt";
    public const string Shapes = "shapes.txt";
    public const string Frequencies = "frequencies.txt";
    public const string Translations = "translations.txt";
    public const string FeedInfo = "feed_info.txt";

    /// <summary>BR-32.1's unconditionally required set.</summary>
    public static readonly string[] Required = [Agency, Routes, Trips, Stops, StopTimes];

    /// <summary>
    /// BR-32.1 also requires the service calendar, as <c>calendar</c> <b>and/or</b>
    /// <c>calendar_dates</c> — a feed may express its whole calendar either way.
    /// </summary>
    public static readonly string[] Calendars = [Calendar, CalendarDates];

    public static readonly string[] Optional = [Shapes, Frequencies, Translations, FeedInfo];
}

/// <summary>
/// The uploaded zip, opened for reading one GTFS file at a time.
/// </summary>
/// <remarks>
/// <b>The entry lookup is deliberately forgiving about a wrapping folder.</b> "Zip the GTFS
/// folder" and "zip the contents of the GTFS folder" produce different archives and every
/// external provider does one or the other — AL-56 makes the file somebody else's work, so the
/// one thing this must not do is refuse a feed for how it was packed. A single common directory
/// prefix is stripped; two different prefixes are not, because that is two feeds in one file and
/// silently picking one would activate a dataset nobody chose.
/// </remarks>
internal sealed class GtfsArchive : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private GtfsArchive(ZipArchive archive, string? prefix, IEnumerable<ZipArchiveEntry> entries)
    {
        _archive = archive;
        Prefix = prefix;

        foreach (var entry in entries)
        {
            var name = entry.FullName;

            if (prefix is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
            }

            if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            _entries[name] = entry;
        }
    }

    /// <summary>The stripped directory prefix, or null when the files sit at the root.</summary>
    public string? Prefix { get; }

    /// <summary>Opens the archive. Throws <see cref="InvalidDataException"/> if it is not a zip.</summary>
    public static GtfsArchive Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        try
        {
            var files = archive.Entries.Where(entry => entry.Name.Length > 0).ToArray();

            return new GtfsArchive(archive, CommonPrefix(files), files);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public bool Contains(string file) => _entries.ContainsKey(file);

    /// <summary>Opens a GTFS file for reading, or null when the feed does not carry it.</summary>
    public GtfsCsvReader? OpenCsv(string file) =>
        _entries.TryGetValue(file, out var entry) ? GtfsCsvReader.Open(entry.Open()) : null;

    public void Dispose() => _archive.Dispose();

    /// <summary>
    /// The one directory every entry sits under, or null when they do not agree on one.
    /// </summary>
    private static string? CommonPrefix(IReadOnlyList<ZipArchiveEntry> entries)
    {
        string? prefix = null;

        foreach (var entry in entries)
        {
            // A macOS "Compress" archive carries a __MACOSX sidecar tree; counting it would make
            // every such upload look like two feeds and defeat the stripping entirely.
            if (entry.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slash = entry.FullName.IndexOf('/', StringComparison.Ordinal);

            if (slash < 0)
            {
                return null;
            }

            var candidate = entry.FullName[..(slash + 1)];

            if (prefix is null)
            {
                prefix = candidate;
                continue;
            }

            if (!string.Equals(prefix, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return prefix;
    }
}
