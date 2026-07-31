using System.Globalization;
using System.Text.Json.Serialization;
using MageRide.Shared.Http;

namespace MageRide.Transit.Gtfs;

/// <summary>One row-level finding, exactly as <c>contracts/transit.yaml</c>'s <c>FeedIssue</c>.</summary>
/// <param name="File">GTFS file name, e.g. <c>stop_times.txt</c>.</param>
/// <param name="Row">
/// 1-based line number **counting the header**, so it is the row a spreadsheet shows. Null for a
/// finding about the feed as a whole rather than about a line.
/// </param>
/// <param name="Code">Stable snake_case key; the message is what changes, never this.</param>
public sealed record FeedIssue(
    string File,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Row,
    string Code,
    string Message)
{
    /// <summary>The one-line form the status endpoint's <c>warnings</c>/<c>errorSummary</c> carry.</summary>
    public string ToLine() => Row is { } row
        ? string.Create(CultureInfo.InvariantCulture, $"{File} row {row}: {Message}")
        : $"{File}: {Message}";
}

/// <summary>What is written to <c>transit.gtfs_feed_versions.validation_report</c> (BR-32.1).</summary>
public sealed record FeedValidationReport(IReadOnlyList<FeedIssue> Errors, IReadOnlyList<FeedIssue> Warnings)
{
    public static readonly FeedValidationReport Empty = new([], []);
}

/// <summary>Everything one validation pass learned about a feed.</summary>
/// <param name="ErrorCount">
/// Total errors found, which is <b>not</b> <c>Report.Errors.Count</c> when the report was capped.
/// The verdict is taken from this, so a feed whose every row is broken still fails.
/// </param>
public sealed record FeedValidationResult(
    FeedValidationReport Report,
    int ErrorCount,
    int WarningCount,
    IReadOnlyDictionary<string, long> Counts,
    string? FeedInfoVersion,
    DateOnly? ServiceStart,
    DateOnly? ServiceEnd)
{
    /// <summary>BR-32.1: any error fails the feed; warnings alone do not.</summary>
    public bool Failed => ErrorCount > 0;
}

/// <summary>The ids the currently active feed uses, for BR-32.1's stable-id warnings.</summary>
public sealed record ActiveFeedIdentity(IReadOnlySet<string> RouteIds, IReadOnlySet<string> StopIds)
{
    public static readonly ActiveFeedIdentity None = new(
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    /// <summary>True before the first import, when there is nothing to compare against.</summary>
    public bool IsEmpty => RouteIds.Count == 0 && StopIds.Count == 0;
}

/// <summary>Stable keys for every finding this validator can produce.</summary>
internal static class FeedIssueCodes
{
    public const string NotAZip = "not_a_zip";
    public const string MissingFile = "missing_file";
    public const string MissingCalendar = "missing_calendar";
    public const string EmptyFile = "empty_file";
    public const string MissingColumn = "missing_column";
    public const string MissingId = "missing_id";
    public const string DuplicateId = "duplicate_id";
    public const string UnknownAgencyId = "unknown_agency_id";
    public const string UnknownRouteId = "unknown_route_id";
    public const string UnknownServiceId = "unknown_service_id";
    public const string UnknownShapeId = "unknown_shape_id";
    public const string UnknownTripId = "unknown_trip_id";
    public const string UnknownStopId = "unknown_stop_id";
    public const string InvalidCoordinate = "invalid_coordinate";
    public const string OutsideServiceArea = "outside_service_area";
    public const string InvalidDate = "invalid_date";
    public const string InvalidTime = "invalid_time";
    public const string InvalidNumber = "invalid_number";
    public const string ServiceWindowExpired = "service_window_expired";

    public const string ServiceWindowShort = "service_window_short";
    public const string RouteIdDisappeared = "route_id_disappeared";
    public const string StopIdDisappeared = "stop_id_disappeared";
    public const string TripWithoutStopTimes = "trip_without_stop_times";
    public const string NoShapes = "no_shapes";
    public const string ReportTruncated = "report_truncated";
}

/// <summary>
/// Accumulates findings up to a cap while counting all of them.
/// </summary>
/// <remarks>
/// The count is what decides the verdict and the list is what an operator downloads; keeping them
/// separate is what lets a feed with 500 000 identical errors both fail and produce a report small
/// enough to open.
/// </remarks>
internal sealed class FeedIssueCollector(int cap)
{
    private readonly List<FeedIssue> _errors = [];
    private readonly List<FeedIssue> _warnings = [];

    public int ErrorCount { get; private set; }

    public int WarningCount { get; private set; }

    public void Error(string file, long? row, string code, string message)
    {
        ErrorCount++;

        if (_errors.Count < cap)
        {
            _errors.Add(new FeedIssue(file, row, code, message));
        }
    }

    public void Warn(string file, long? row, string code, string message)
    {
        WarningCount++;

        if (_warnings.Count < cap)
        {
            _warnings.Add(new FeedIssue(file, row, code, message));
        }
    }

    public FeedValidationReport Build()
    {
        var errors = _errors;
        var warnings = _warnings;

        if (ErrorCount > errors.Count)
        {
            errors =
            [
                .. errors,
                new FeedIssue(
                    "-", null, FeedIssueCodes.ReportTruncated,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{ErrorCount} errors were found; this report lists the first {_errors.Count}.")),
            ];
        }

        if (WarningCount > warnings.Count)
        {
            warnings =
            [
                .. warnings,
                new FeedIssue(
                    "-", null, FeedIssueCodes.ReportTruncated,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{WarningCount} warnings were found; this report lists the first {_warnings.Count}.")),
            ];
        }

        return new FeedValidationReport(errors, warnings);
    }
}

/// <summary>Serialiser settings for the <c>counts</c> and <c>validation_report</c> columns.</summary>
internal static class GtfsJson
{
    /// <summary>
    /// <c>counts</c> is keyed by GTFS <b>file name</b>, which is data and not a property name —
    /// the kernel's camelCase dictionary policy would answer <c>stopTimes</c> for
    /// <c>stop_times</c>, and SCR-AP-016's counts grid is labelled from the same keys.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions Counts = BuildCounts();

    private static System.Text.Json.JsonSerializerOptions BuildCounts()
    {
        var options = new System.Text.Json.JsonSerializerOptions(MageRideJson.StorageOptions);
        options.Converters.Add(new LiteralKeyDictionaryConverter<long>());
        options.MakeReadOnly();

        return options;
    }
}
