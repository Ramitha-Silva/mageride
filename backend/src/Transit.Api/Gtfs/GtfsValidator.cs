using System.Globalization;
using MageRide.Shared.Time;
using MageRide.Transit.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Transit.Gtfs;

/// <summary>BR-32.1's quality gate, run off the request path against an uploaded zip.</summary>
public interface IGtfsValidator
{
    FeedValidationResult Validate(Stream zip, ActiveFeedIdentity active);
}

/// <summary>
/// <inheritdoc cref="IGtfsValidator"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only quality gate MageRide enforces</b> (AL-56). The feed is somebody else's
/// file — launch and every refresh — so nothing upstream of this checks it, and nothing
/// downstream can: activation is a bulk load into tables whose foreign keys would abort the swap
/// halfway with no report of what was wrong.
/// </para>
/// <para>
/// <b>Errors block, warnings do not.</b> BR-32.1 draws that line, and it is the line between "this
/// dataset would break route matching" and "somebody should look at this". A stop 400 km out to
/// sea is the first; a service window that ends in three weeks is the second — the feed still
/// works, it just needs replacing soon.
/// </para>
/// <para>
/// <b>One pass per file, in dependency order.</b> Ids are collected before the files that
/// reference them are read, so every referential check is a set lookup and no file is read twice.
/// The sets are bounded by the feed's entity counts (routes, trips, stops — thousands), never by
/// its row counts: <c>stop_times.txt</c> is streamed and the only thing kept from it is the
/// duplicate-key set the primary key of <c>transit.gtfs_stop_times</c> would otherwise reject
/// mid-import.
/// </para>
/// </remarks>
internal sealed class GtfsValidator(IOptions<TransitOptions> options, TimeProvider clock) : IGtfsValidator
{
    /// <summary>
    /// BR-32.1's service area: 5.7–10.0 °N, 79.4–82.1 °E.
    /// </summary>
    /// <remarks>
    /// A constant rather than a setting. It is not a tuning knob — it is the statement that this
    /// platform operates in Sri Lanka, and a feed with stops outside it is a feed for somewhere
    /// else. Widening it is a spec change (BR-32.1), not a deployment decision.
    /// </remarks>
    public const double MinLat = 5.7;

    /// <inheritdoc cref="MinLat"/>
    public const double MaxLat = 10.0;

    /// <inheritdoc cref="MinLat"/>
    public const double MinLng = 79.4;

    /// <inheritdoc cref="MinLat"/>
    public const double MaxLng = 82.1;

    private readonly TransitOptions.GtfsOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.Gtfs;

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public FeedValidationResult Validate(Stream zip, ActiveFeedIdentity active)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(active);

        var issues = new FeedIssueCollector(_options.MaxReportedIssues);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        GtfsArchive archive;

        try
        {
            archive = GtfsArchive.Open(zip);
        }
        catch (InvalidDataException exception)
        {
            issues.Error("-", null, FeedIssueCodes.NotAZip, $"The upload is not a readable zip archive: {exception.Message}");

            return Result(issues, counts, null, null, null);
        }

        using (archive)
        {
            if (!RequiredFilesPresent(archive, issues))
            {
                // Nothing downstream can be checked without them, and a hundred consequential
                // "unknown_stop_id" rows would bury the one finding that explains the feed.
                return Result(issues, counts, null, null, null);
            }

            var agencies = ReadIds(archive, GtfsFiles.Agency, "agency_id", issues, counts, idRequired: false);
            var stops = ReadStops(archive, issues, counts);
            var routes = ReadRoutes(archive, agencies, issues, counts);
            var services = ReadCalendars(archive, issues, counts, out var serviceStart, out var serviceEnd);
            var shapes = ReadShapes(archive, issues, counts);
            var trips = ReadTrips(archive, routes, services, shapes, issues, counts);

            ReadStopTimes(archive, trips, stops, issues, counts);
            ReadFrequencies(archive, trips, issues, counts);

            CheckServiceWindow(serviceStart, serviceEnd, issues);
            CheckStableIds(active, routes, stops, issues);

            if (!archive.Contains(GtfsFiles.Shapes))
            {
                // Not an error — `shapes.txt` is optional in GTFS and in BR-32.1. But every
                // SCR-PA-009 option draws its route from a shape, so a feed without one produces
                // options with no line on the map, and that is worth saying before activation.
                issues.Warn(
                    GtfsFiles.Shapes, null, FeedIssueCodes.NoShapes,
                    "The feed carries no shapes.txt, so route options will have no polyline to draw.");
            }

            return Result(issues, counts, ReadFeedInfoVersion(archive), serviceStart, serviceEnd);
        }
    }

    private static FeedValidationResult Result(
        FeedIssueCollector issues,
        Dictionary<string, long> counts,
        string? feedInfoVersion,
        DateOnly? serviceStart,
        DateOnly? serviceEnd) =>
        new(issues.Build(), issues.ErrorCount, issues.WarningCount, counts, feedInfoVersion, serviceStart, serviceEnd);

    // -----------------------------------------------------------------------------------------
    // Files
    // -----------------------------------------------------------------------------------------

    private static bool RequiredFilesPresent(GtfsArchive archive, FeedIssueCollector issues)
    {
        var complete = true;

        foreach (var file in GtfsFiles.Required)
        {
            if (!archive.Contains(file))
            {
                complete = false;
                issues.Error(file, null, FeedIssueCodes.MissingFile, "Required GTFS file is missing from the upload.");
            }
        }

        if (!GtfsFiles.Calendars.Any(archive.Contains))
        {
            complete = false;
            issues.Error(
                GtfsFiles.Calendar, null, FeedIssueCodes.MissingCalendar,
                "A service calendar is required: the feed must carry calendar.txt, calendar_dates.txt, or both.");
        }

        return complete;
    }

    /// <summary>Reads a file's primary id column into a set, reporting duplicates and blanks.</summary>
    private static HashSet<string> ReadIds(
        GtfsArchive archive,
        string file,
        string idColumn,
        FeedIssueCollector issues,
        Dictionary<string, long> counts,
        bool idRequired = true)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        using var reader = archive.OpenCsv(file);

        if (reader is null)
        {
            return ids;
        }

        var hasColumn = reader.Has(idColumn);

        if (idRequired && !hasColumn)
        {
            issues.Error(file, 1, FeedIssueCodes.MissingColumn, $"Required column '{idColumn}' is not declared.");
        }

        var rows = 0L;

        while (reader.Read())
        {
            rows++;

            if (!hasColumn)
            {
                continue;
            }

            var id = reader[idColumn];

            if (id is null)
            {
                // agency.txt may omit agency_id when the feed has exactly one agency, which is
                // why the column is optional there and required everywhere else.
                if (idRequired)
                {
                    issues.Error(file, reader.Row, FeedIssueCodes.MissingId, $"'{idColumn}' is blank.");
                }

                continue;
            }

            if (!ids.Add(id))
            {
                issues.Error(file, reader.Row, FeedIssueCodes.DuplicateId, $"'{idColumn}' {id} appears more than once.");
            }
        }

        counts[Key(file)] = rows;

        if (rows == 0)
        {
            issues.Error(file, null, FeedIssueCodes.EmptyFile, "The file declares a header and no rows.");
        }

        return ids;
    }

    private HashSet<string> ReadStops(GtfsArchive archive, FeedIssueCollector issues, Dictionary<string, long> counts)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        using var reader = archive.OpenCsv(GtfsFiles.Stops);

        if (reader is null)
        {
            return ids;
        }

        var rows = 0L;

        while (reader.Read())
        {
            rows++;

            var id = reader["stop_id"];

            if (id is null)
            {
                issues.Error(GtfsFiles.Stops, reader.Row, FeedIssueCodes.MissingId, "'stop_id' is blank.");
                continue;
            }

            if (!ids.Add(id))
            {
                issues.Error(GtfsFiles.Stops, reader.Row, FeedIssueCodes.DuplicateId, $"'stop_id' {id} appears more than once.");
            }

            CheckStopPosition(reader, id, issues);
        }

        counts[Key(GtfsFiles.Stops)] = rows;

        if (rows == 0)
        {
            issues.Error(GtfsFiles.Stops, null, FeedIssueCodes.EmptyFile, "The file declares a header and no rows.");
        }

        return ids;
    }

    /// <summary>
    /// BR-32.1's "every stop within the Sri Lanka bounding box".
    /// </summary>
    /// <remarks>
    /// GTFS makes <c>stop_lat</c>/<c>stop_lon</c> conditionally required — a boarding area or a
    /// generic node (<c>location_type</c> 3/4) may legitimately have none — so the coordinate is
    /// demanded of the location types that are places a bus stops at, and the bbox is applied to
    /// whatever coordinate is present. A halt with no position is not a halt this platform can
    /// match a corridor against.
    /// </remarks>
    private static void CheckStopPosition(GtfsCsvReader reader, string stopId, FeedIssueCollector issues)
    {
        var latText = reader["stop_lat"];
        var lngText = reader["stop_lon"];
        var locationType = reader["location_type"];
        var positionRequired = locationType is null or "0" or "1" or "2";

        if (latText is null || lngText is null)
        {
            if (positionRequired)
            {
                issues.Error(
                    GtfsFiles.Stops, reader.Row, FeedIssueCodes.InvalidCoordinate,
                    $"Stop {stopId} has no stop_lat/stop_lon.");
            }

            return;
        }

        if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(lngText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
        {
            issues.Error(
                GtfsFiles.Stops, reader.Row, FeedIssueCodes.InvalidCoordinate,
                $"Stop {stopId} has an unreadable coordinate ({latText}, {lngText}).");
            return;
        }

        if (lat is < MinLat or > MaxLat || lng is < MinLng or > MaxLng)
        {
            issues.Error(
                GtfsFiles.Stops, reader.Row, FeedIssueCodes.OutsideServiceArea,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Stop {stopId} at ({lat}, {lng}) is outside Sri Lanka ({MinLat}–{MaxLat} °N, {MinLng}–{MaxLng} °E)."));
        }
    }

    private static HashSet<string> ReadRoutes(
        GtfsArchive archive, HashSet<string> agencies, FeedIssueCollector issues, Dictionary<string, long> counts)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        using var reader = archive.OpenCsv(GtfsFiles.Routes);

        if (reader is null)
        {
            return ids;
        }

        var rows = 0L;

        while (reader.Read())
        {
            rows++;

            var id = reader["route_id"];

            if (id is null)
            {
                issues.Error(GtfsFiles.Routes, reader.Row, FeedIssueCodes.MissingId, "'route_id' is blank.");
            }
            else if (!ids.Add(id))
            {
                issues.Error(GtfsFiles.Routes, reader.Row, FeedIssueCodes.DuplicateId, $"'route_id' {id} appears more than once.");
            }

            // Only checked when the feed names agencies at all: a single-agency feed may omit
            // agency_id on both sides, which GTFS allows and which is not a broken reference.
            if (reader["agency_id"] is { } agencyId && agencies.Count > 0 && !agencies.Contains(agencyId))
            {
                issues.Error(
                    GtfsFiles.Routes, reader.Row, FeedIssueCodes.UnknownAgencyId,
                    $"Route {id} names agency_id {agencyId}, which is not in agency.txt.");
            }

            if (reader["route_type"] is { } routeType &&
                !int.TryParse(routeType, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                issues.Error(
                    GtfsFiles.Routes, reader.Row, FeedIssueCodes.InvalidNumber,
                    $"Route {id} has a non-numeric route_type '{routeType}'.");
            }
        }

        counts[Key(GtfsFiles.Routes)] = rows;

        if (rows == 0)
        {
            issues.Error(GtfsFiles.Routes, null, FeedIssueCodes.EmptyFile, "The file declares a header and no rows.");
        }

        return ids;
    }

    /// <summary>
    /// Every <c>service_id</c> the feed defines, from either calendar file, plus the window they
    /// span.
    /// </summary>
    /// <remarks>
    /// Both files feed one set because GTFS lets a service be defined entirely by exceptions:
    /// a feed with no <c>calendar.txt</c> at all is valid and common, and treating
    /// <c>calendar_dates.txt</c> as an override-only file would report every one of its services
    /// as an unknown reference.
    /// </remarks>
    private static HashSet<string> ReadCalendars(
        GtfsArchive archive,
        FeedIssueCollector issues,
        Dictionary<string, long> counts,
        out DateOnly? serviceStart,
        out DateOnly? serviceEnd)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        serviceStart = null;
        serviceEnd = null;

        using (var reader = archive.OpenCsv(GtfsFiles.Calendar))
        {
            var rows = 0L;

            while (reader is not null && reader.Read())
            {
                rows++;

                var id = reader["service_id"];

                if (id is null)
                {
                    issues.Error(GtfsFiles.Calendar, reader.Row, FeedIssueCodes.MissingId, "'service_id' is blank.");
                }
                else if (!ids.Add(id))
                {
                    issues.Error(
                        GtfsFiles.Calendar, reader.Row, FeedIssueCodes.DuplicateId,
                        $"'service_id' {id} appears more than once.");
                }

                Span(reader, GtfsFiles.Calendar, "start_date", issues, ref serviceStart, ref serviceEnd);
                Span(reader, GtfsFiles.Calendar, "end_date", issues, ref serviceStart, ref serviceEnd);
            }

            if (reader is not null)
            {
                counts[Key(GtfsFiles.Calendar)] = rows;
            }
        }

        using (var reader = archive.OpenCsv(GtfsFiles.CalendarDates))
        {
            var rows = 0L;

            while (reader is not null && reader.Read())
            {
                rows++;

                if (reader["service_id"] is { } id)
                {
                    ids.Add(id);
                }
                else
                {
                    issues.Error(GtfsFiles.CalendarDates, reader.Row, FeedIssueCodes.MissingId, "'service_id' is blank.");
                }

                // An exception that *removes* service cannot extend the window, but a feed that
                // uses calendar_dates alone has no other statement of when it runs, so both
                // exception types are taken — the alternative is no window at all for that feed.
                Span(reader, GtfsFiles.CalendarDates, "date", issues, ref serviceStart, ref serviceEnd);
            }

            if (reader is not null)
            {
                counts[Key(GtfsFiles.CalendarDates)] = rows;
            }
        }

        return ids;
    }

    private static void Span(
        GtfsCsvReader reader,
        string file,
        string column,
        FeedIssueCollector issues,
        ref DateOnly? start,
        ref DateOnly? end)
    {
        if (reader[column] is not { } text)
        {
            return;
        }

        if (!DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            issues.Error(file, reader.Row, FeedIssueCodes.InvalidDate, $"'{column}' is '{text}', which is not a GTFS YYYYMMDD date.");
            return;
        }

        start = start is null || date < start ? date : start;
        end = end is null || date > end ? date : end;
    }

    private static HashSet<string> ReadShapes(
        GtfsArchive archive, FeedIssueCollector issues, Dictionary<string, long> counts)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        using var reader = archive.OpenCsv(GtfsFiles.Shapes);

        if (reader is null)
        {
            return ids;
        }

        var points = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0L;

        while (reader.Read())
        {
            rows++;

            var id = reader["shape_id"];

            if (id is null)
            {
                issues.Error(GtfsFiles.Shapes, reader.Row, FeedIssueCodes.MissingId, "'shape_id' is blank.");
                continue;
            }

            ids.Add(id);

            var sequence = reader["shape_pt_sequence"];

            if (sequence is null ||
                !int.TryParse(sequence, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                issues.Error(
                    GtfsFiles.Shapes, reader.Row, FeedIssueCodes.InvalidNumber,
                    $"Shape {id} has a shape_pt_sequence of '{sequence}', which is not a whole number.");
                continue;
            }

            // (shape_id, seq) is the primary key of transit.gtfs_shapes: a duplicate would abort
            // the import halfway through the swap rather than fail here with a row number on it.
            if (!points.Add(string.Concat(id, "\u001f", sequence)))
            {
                issues.Error(
                    GtfsFiles.Shapes, reader.Row, FeedIssueCodes.DuplicateId,
                    $"Shape {id} has two points at sequence {sequence}.");
            }
        }

        counts[Key(GtfsFiles.Shapes)] = rows;

        return ids;
    }

    private static HashSet<string> ReadTrips(
        GtfsArchive archive,
        HashSet<string> routes,
        HashSet<string> services,
        HashSet<string> shapes,
        FeedIssueCollector issues,
        Dictionary<string, long> counts)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        using var reader = archive.OpenCsv(GtfsFiles.Trips);

        if (reader is null)
        {
            return ids;
        }

        var rows = 0L;

        while (reader.Read())
        {
            rows++;

            var id = reader["trip_id"];

            if (id is null)
            {
                issues.Error(GtfsFiles.Trips, reader.Row, FeedIssueCodes.MissingId, "'trip_id' is blank.");
                continue;
            }

            if (!ids.Add(id))
            {
                issues.Error(GtfsFiles.Trips, reader.Row, FeedIssueCodes.DuplicateId, $"'trip_id' {id} appears more than once.");
            }

            var routeId = reader["route_id"];

            if (routeId is null)
            {
                issues.Error(GtfsFiles.Trips, reader.Row, FeedIssueCodes.MissingId, $"Trip {id} has a blank route_id.");
            }
            else if (!routes.Contains(routeId))
            {
                issues.Error(
                    GtfsFiles.Trips, reader.Row, FeedIssueCodes.UnknownRouteId,
                    $"Trip {id} names route_id {routeId}, which is not in routes.txt.");
            }

            var serviceId = reader["service_id"];

            if (serviceId is null)
            {
                issues.Error(GtfsFiles.Trips, reader.Row, FeedIssueCodes.MissingId, $"Trip {id} has a blank service_id.");
            }
            else if (!services.Contains(serviceId))
            {
                issues.Error(
                    GtfsFiles.Trips, reader.Row, FeedIssueCodes.UnknownServiceId,
                    $"Trip {id} names service_id {serviceId}, which no calendar defines.");
            }

            if (reader["shape_id"] is { } shapeId && !shapes.Contains(shapeId))
            {
                issues.Error(
                    GtfsFiles.Trips, reader.Row, FeedIssueCodes.UnknownShapeId,
                    $"Trip {id} names shape_id {shapeId}, which is not in shapes.txt.");
            }
        }

        counts[Key(GtfsFiles.Trips)] = rows;

        if (rows == 0)
        {
            issues.Error(GtfsFiles.Trips, null, FeedIssueCodes.EmptyFile, "The file declares a header and no rows.");
        }

        return ids;
    }

    private static void ReadStopTimes(
        GtfsArchive archive,
        HashSet<string> trips,
        HashSet<string> stops,
        FeedIssueCollector issues,
        Dictionary<string, long> counts)
    {
        using var reader = archive.OpenCsv(GtfsFiles.StopTimes);

        if (reader is null)
        {
            return;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var served = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0L;

        while (reader.Read())
        {
            rows++;

            var tripId = reader["trip_id"];
            var stopId = reader["stop_id"];
            var sequence = reader["stop_sequence"];

            if (tripId is null)
            {
                issues.Error(GtfsFiles.StopTimes, reader.Row, FeedIssueCodes.MissingId, "'trip_id' is blank.");
            }
            else
            {
                served.Add(tripId);

                if (!trips.Contains(tripId))
                {
                    issues.Error(
                        GtfsFiles.StopTimes, reader.Row, FeedIssueCodes.UnknownTripId,
                        $"trip_id {tripId} is not in trips.txt.");
                }
            }

            if (stopId is null)
            {
                issues.Error(GtfsFiles.StopTimes, reader.Row, FeedIssueCodes.MissingId, "'stop_id' is blank.");
            }
            else if (!stops.Contains(stopId))
            {
                issues.Error(
                    GtfsFiles.StopTimes, reader.Row, FeedIssueCodes.UnknownStopId,
                    $"stop_id {stopId} is not in stops.txt.");
            }

            if (sequence is null ||
                !int.TryParse(sequence, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                issues.Error(
                    GtfsFiles.StopTimes, reader.Row, FeedIssueCodes.InvalidNumber,
                    $"'stop_sequence' is '{sequence}', which is not a whole number.");
            }
            else if (tripId is not null && !keys.Add(string.Concat(tripId, "\u001f", sequence)))
            {
                // (trip_id, stop_sequence) is the primary key of transit.gtfs_stop_times.
                issues.Error(
                    GtfsFiles.StopTimes, reader.Row, FeedIssueCodes.DuplicateId,
                    $"Trip {tripId} calls at sequence {sequence} twice.");
            }

            CheckTime(reader, "arrival_time", issues);
            CheckTime(reader, "departure_time", issues);
        }

        counts[Key(GtfsFiles.StopTimes)] = rows;

        if (rows == 0)
        {
            issues.Error(GtfsFiles.StopTimes, null, FeedIssueCodes.EmptyFile, "The file declares a header and no rows.");
        }

        foreach (var trip in trips)
        {
            if (!served.Contains(trip))
            {
                // Not a broken reference — the trip row is well formed — but it is a working the
                // matcher will never offer, because a pattern is a stop sequence and this one has
                // none.
                issues.Warn(
                    GtfsFiles.Trips, null, FeedIssueCodes.TripWithoutStopTimes,
                    $"Trip {trip} has no stop_times rows and will not appear as a route option.");
            }
        }
    }

    /// <summary>
    /// GTFS times may exceed 24 hours ("25:10:00" is 01:10 the next morning on the same service
    /// day), which is why <c>transit.gtfs_stop_times.arr</c> is an INTERVAL and not a TIME.
    /// </summary>
    private static void CheckTime(GtfsCsvReader reader, string column, FeedIssueCollector issues)
    {
        if (reader[column] is { } text && !GtfsTime.TryParse(text, out _))
        {
            issues.Error(
                GtfsFiles.StopTimes, reader.Row, FeedIssueCodes.InvalidTime,
                $"'{column}' is '{text}', which is not a GTFS H:MM:SS time.");
        }
    }

    private static void ReadFrequencies(
        GtfsArchive archive, HashSet<string> trips, FeedIssueCollector issues, Dictionary<string, long> counts)
    {
        using var reader = archive.OpenCsv(GtfsFiles.Frequencies);

        if (reader is null)
        {
            return;
        }

        var rows = 0L;

        while (reader.Read())
        {
            rows++;

            if (reader["trip_id"] is { } tripId && !trips.Contains(tripId))
            {
                issues.Error(
                    GtfsFiles.Frequencies, reader.Row, FeedIssueCodes.UnknownTripId,
                    $"trip_id {tripId} is not in trips.txt.");
            }
        }

        counts[Key(GtfsFiles.Frequencies)] = rows;
    }

    private static string? ReadFeedInfoVersion(GtfsArchive archive)
    {
        using var reader = archive.OpenCsv(GtfsFiles.FeedInfo);

        return reader is not null && reader.Read() ? reader["feed_version"] : null;
    }

    // -----------------------------------------------------------------------------------------
    // Feed-level rules
    // -----------------------------------------------------------------------------------------

    /// <summary>BR-32.1: <c>service_end ≥ today</c>; warn if it is less than 30 days ahead.</summary>
    /// <remarks>
    /// "Today" is the <b>Asia/Colombo</b> date (D-38). A feed that expires tonight local time is
    /// still usable today, and evaluating this in UTC would reject it five and a half hours early.
    /// </remarks>
    private void CheckServiceWindow(DateOnly? start, DateOnly? end, FeedIssueCollector issues)
    {
        if (end is not { } serviceEnd)
        {
            return;
        }

        var today = BusinessCalendar.Today(_clock);

        if (serviceEnd < today)
        {
            issues.Error(
                GtfsFiles.Calendar, null, FeedIssueCodes.ServiceWindowExpired,
                $"The feed's service ends on {serviceEnd:yyyy-MM-dd}, which is already past ({today:yyyy-MM-dd} in Asia/Colombo).");

            return;
        }

        var horizon = today.AddDays(_options.ServiceWindowWarnDays);

        if (serviceEnd < horizon)
        {
            issues.Warn(
                GtfsFiles.Calendar, null, FeedIssueCodes.ServiceWindowShort,
                $"The feed's service ends on {serviceEnd:yyyy-MM-dd}, less than {_options.ServiceWindowWarnDays} days away; a replacement feed will be needed before then.");
        }

        if (start is { } serviceStart && serviceStart > today)
        {
            issues.Warn(
                GtfsFiles.Calendar, null, FeedIssueCodes.ServiceWindowShort,
                $"The feed's service does not begin until {serviceStart:yyyy-MM-dd}; activating it today leaves every corridor without a route option until then.");
        }
    }

    /// <summary>
    /// BR-32.1's stable-id warnings, defined against the currently active feed (AL-56).
    /// </summary>
    /// <remarks>
    /// A <c>route_id</c> or <c>stop_id</c> that disappears between versions is not invalid — but it
    /// breaks every reference an app or a saved search holds to it, and it is almost always an
    /// export that regenerated its ids rather than a route that stopped running. Only
    /// disappearances are reported: new ids are what a growing feed is made of.
    /// </remarks>
    private static void CheckStableIds(
        ActiveFeedIdentity active, HashSet<string> routes, HashSet<string> stops, FeedIssueCollector issues)
    {
        if (active.IsEmpty)
        {
            return;
        }

        foreach (var routeId in active.RouteIds)
        {
            if (!routes.Contains(routeId))
            {
                issues.Warn(
                    GtfsFiles.Routes, null, FeedIssueCodes.RouteIdDisappeared,
                    $"route_id {routeId} is in the active feed but not in this one; ids should stay stable across versions.");
            }
        }

        foreach (var stopId in active.StopIds)
        {
            if (!stops.Contains(stopId))
            {
                issues.Warn(
                    GtfsFiles.Stops, null, FeedIssueCodes.StopIdDisappeared,
                    $"stop_id {stopId} is in the active feed but not in this one; ids should stay stable across versions.");
            }
        }
    }

    /// <summary>The <c>counts</c> key for a file: its GTFS base name, e.g. <c>stop_times</c>.</summary>
    private static string Key(string file) => file[..^".txt".Length];
}

/// <summary>GTFS clock times, which may run past midnight into the same service day.</summary>
internal static class GtfsTime
{
    /// <summary>Parses <c>H:MM:SS</c> or <c>HH:MM:SS</c>, hours unbounded.</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var parts = text.Split(':');

        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if (minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            return false;
        }

        value = new TimeSpan(hours, minutes, seconds);

        return true;
    }
}
