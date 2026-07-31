using MageRide.Fleet.Configuration;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Operations;

/// <summary>One polygon an operator is defining, before it has been checked.</summary>
public sealed record GeofenceDraft(string? Name, IReadOnlyList<GeoPointDraft>? Polygon);

/// <summary>A vertex as it arrives on the wire, before its bounds have been checked.</summary>
public sealed record GeoPointDraft(double? Lat, double? Lng);

/// <summary>The live map (US-13.3), the analytics table (US-13.4) and the geofences (US-13.5).</summary>
public interface IFleetInsightsService
{
    Task<(IReadOnlyList<FleetVehiclePosition> Vehicles, DateTimeOffset AsOf)> ReadMapAsync(
        Guid fleetId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VehicleAnalytics>> ReadAnalyticsAsync(
        Guid fleetId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);

    Task<IReadOnlyList<FleetGeofence>> ListGeofencesAsync(Guid fleetId, CancellationToken cancellationToken);

    Task<int> ReplaceGeofencesAsync(
        Guid fleetId, IReadOnlyList<GeofenceDraft>? drafts, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IFleetInsightsService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>The map and the analytics are read through the row-level-security scope and nowhere else.</b>
/// Both enter <c>IFleetScopedReader</c>, which does <c>SET LOCAL ROLE mageride_fleet_reader</c> and
/// sets <c>app.fleet_id</c> — so the answer to "which vehicles are mine" is the database's, and a
/// forgotten <c>WHERE</c> in this file would return the caller's own vehicles rather than
/// everybody's. That is the C059 definition of done, and <c>RowLevelSecurityTests</c> asserts it by
/// connecting as a real non-superuser login with none of this code in the path.
/// </para>
/// <para>
/// <b>The analytics period is evaluated in Asia/Colombo (D-13).</b> A date range an operator types
/// is local days: "1st to 7th" means the seven days their drivers worked, not seven UTC days
/// starting five and a half hours late. <c>BusinessCalendar</c> is the platform's single crossing
/// point between the two.
/// </para>
/// <para>
/// <b>Geofence CRUD only — the alerting is Phase 3 and is deliberately not built.</b> US-13.5's
/// route-deviation and geofence alerts have no producer anywhere in this build; storing the
/// polygons now costs nothing and lets an operator prepare, and <c>GET /alerts</c> answers an empty
/// page so the portal can render its empty state without a later breaking change.
/// </para>
/// </remarks>
internal sealed class FleetInsightsService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetScopedReader scopedReader,
    IFleetInsightsRepository insights,
    IOptions<FleetOptions> options,
    TimeProvider clock,
    ILogger<FleetInsightsService> logger) : IFleetInsightsService
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<(IReadOnlyList<FleetVehiclePosition> Vehicles, DateTimeOffset AsOf)> ReadMapAsync(
        Guid fleetId, CancellationToken cancellationToken)
    {
        var asOf = _clock.GetUtcNow();

        var positions = await scopedReader.ReadAsync(
            fleetId,
            (connection, transaction) => insights.ReadMapAsync(
                connection, transaction, asOf - _options.MapStaleAfter, cancellationToken),
            cancellationToken);

        return (positions, asOf);
    }

    public Task<IReadOnlyList<VehicleAnalytics>> ReadAnalyticsAsync(
        Guid fleetId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var today = BusinessCalendar.Today(_clock);

        // Defaults that answer the screen SCR-FP-009 opens on: the last thirty Colombo days, ending
        // at the end of today. `to` is inclusive of the day named, so the range runs to the start of
        // the day after — a report "to the 7th" that stopped at midnight on the 7th would drop a
        // whole day's driving and look like a quiet Friday.
        var last = to ?? today;
        var first = from ?? last.AddDays(-29);

        if (first > last)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["from"] = ["from must be on or before to."],
            });
        }

        var days = last.DayNumber - first.DayNumber + 1;

        if (days > _options.MaxAnalyticsDays)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["from"] = [$"The range is {days} days; at most {_options.MaxAnalyticsDays} may be reported at once."],
            });
        }

        var (start, _) = BusinessCalendar.DayRange(first);
        var (_, end) = BusinessCalendar.DayRange(last);

        return scopedReader.ReadAsync(
            fleetId,
            (connection, transaction) => insights.ReadAnalyticsAsync(
                connection, transaction, start, end, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<FleetGeofence>> ListGeofencesAsync(
        Guid fleetId, CancellationToken cancellationToken) =>
        scopedReader.ReadAsync(
            fleetId,
            (connection, transaction) => insights.ListGeofencesAsync(
                connection, transaction, fleetId, cancellationToken),
            cancellationToken);

    public async Task<int> ReplaceGeofencesAsync(
        Guid fleetId, IReadOnlyList<GeofenceDraft>? drafts, CancellationToken cancellationToken)
    {
        var geofences = Validate(drafts ?? []);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var stored = await insights.ReplaceGeofencesAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            fleetId,
            [.. geofences.Select(geofence => new FleetGeofence(Guid.Empty, fleetId, geofence.Name, geofence.Polygon))],
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Fleet {FleetId} replaced its operational geofences with {Count}. Route-deviation and geofence alerting "
            + "is Phase 3 (US-13.5) and consumes these; nothing raises an alert today.",
            fleetId,
            stored);

        return stored;
    }

    /// <summary>
    /// Checks the rings before any of them is written.
    /// </summary>
    /// <remarks>
    /// All-or-nothing, because the route replaces a set: refusing halfway would leave an operator
    /// with the fences that happened to sort first. The closure check is the one that matters —
    /// PostGIS refuses an unclosed ring with a message about linear rings, which is not something to
    /// hand to a portal.
    /// </remarks>
    private List<(string? Name, IReadOnlyList<GeoPoint> Polygon)> Validate(IReadOnlyList<GeofenceDraft> drafts)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (drafts.Count > _options.MaxGeofences)
        {
            errors["geofences"] = [$"At most {_options.MaxGeofences} geofences may be defined."];
        }

        var geofences = new List<(string? Name, IReadOnlyList<GeoPoint> Polygon)>(drafts.Count);

        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            var field = $"geofences[{index}]";
            var name = draft.Name?.Trim();

            if (string.IsNullOrEmpty(name) || name.Length > 120)
            {
                errors[$"{field}.name"] = ["name is required and is at most 120 characters."];
                continue;
            }

            var ring = draft.Polygon ?? [];

            if (ring.Count < 4)
            {
                errors[$"{field}.polygon"] = ["polygon is a closed ring of at least 4 points."];
                continue;
            }

            if (ring.Count > _options.MaxGeofenceVertices)
            {
                errors[$"{field}.polygon"] = [$"polygon has at most {_options.MaxGeofenceVertices} points."];
                continue;
            }

            var points = new List<GeoPoint>(ring.Count);
            var malformed = false;

            foreach (var point in ring)
            {
                if (point.Lat is not { } lat || point.Lng is not { } lng
                    || double.IsNaN(lat) || double.IsNaN(lng)
                    || lat is < -90 or > 90 || lng is < -180 or > 180)
                {
                    errors[$"{field}.polygon"] = ["every point needs a lat in [-90,90] and a lng in [-180,180]."];
                    malformed = true;
                    break;
                }

                points.Add(new GeoPoint(lat, lng));
            }

            if (malformed)
            {
                continue;
            }

            // The contract says "Closed ring; the first and last points must match", and PostGIS
            // requires it too. Checked rather than closed for the caller: silently appending the
            // first point would turn a ring somebody meant to draw differently into one that merely
            // parses.
            if (points[0] != points[^1])
            {
                errors[$"{field}.polygon"] = ["the first and last points of the ring must be identical."];
                continue;
            }

            geofences.Add((name, points));
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        return geofences;
    }
}
