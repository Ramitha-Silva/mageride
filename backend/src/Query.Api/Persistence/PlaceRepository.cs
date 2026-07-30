using Dapper;
using MageRide.Shared.Primitives;

namespace MageRide.Query.Persistence;

/// <summary>Where a place suggestion came from.</summary>
/// <remarks>
/// BR-23.1's prediction set has three sources and a client renders them differently — a saved Home
/// gets a house icon, a recent destination a clock, a geocoded match a pin. What the list must
/// <b>never</b> contain is a fourth kind: a bus route (AL-17).
/// </remarks>
public static class PlaceSources
{
    /// <summary>
    /// The self-hosted Nominatim (D-14). Spelled <c>nominatim</c> because <c>query.yaml</c>'s
    /// <c>GeocodedPlace.source</c> enum spells it that way and the contract wins.
    /// </summary>
    public const string Geocoded = "nominatim";

    /// <summary><c>iam.saved_addresses</c> — Home, Work, or a labelled address (AL-26, US-22.2).</summary>
    public const string Saved = "saved";

    /// <summary>A destination this user has been to before.</summary>
    public const string Recent = "recent";
}

/// <summary>A place the caller has already told the platform about.</summary>
public sealed record KnownPlace(
    GeoPoint Point, string DisplayName, string? Line1, string? City, string Source, string? Label);

/// <summary>The caller's own places — the non-geocoded half of BR-23.1's predictions.</summary>
public interface IPlaceRepository
{
    /// <summary>Saved addresses whose label or lines match <paramref name="term"/>.</summary>
    Task<IReadOnlyList<KnownPlace>> SavedAsync(
        Guid userId, string? term, int limit, CancellationToken cancellationToken);

    /// <summary>Recent distinct drop-off points from this user's own rides.</summary>
    Task<IReadOnlyList<KnownPlace>> RecentAsync(Guid userId, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPlaceRepository"/>
/// <remarks>
/// <para>
/// <b>This repository has no access to <c>spatial.routes</c>, and that is the mechanism behind
/// AL-17.</b> "Destination search returns geocoded places and saved/recent only. No route-number
/// rows" is not a filter applied to a wider result set — a filter can be bypassed, reordered or
/// forgotten. There is simply no query here, or anywhere on the search path, that can return a route:
/// the only relations reachable are <c>iam.saved_addresses</c> and <c>rides.rides</c>, and the only
/// other source is a geocoder that indexes an OSM extract of places. A passenger typing "138" gets
/// whatever Nominatim thinks "138" is — a house number — and never a bus route, because no code exists
/// that could produce one.
/// </para>
/// <para>
/// <b>Recents come from the caller's own drop-offs, not from a search history.</b> A place somebody
/// actually travelled to is a stronger prediction than a place they once typed, and it needs no new
/// table: <c>ix_rides_passenger_created</c> already orders a passenger's rides. Rides where the caller
/// was the <em>driver</em> are excluded — a driver's drop-offs are their passengers' destinations, and
/// offering them back is both useless and a disclosure.
/// </para>
/// </remarks>
public sealed class PlaceRepository(IQueryConnectionFactory connections) : IPlaceRepository
{
    /// <summary>
    /// Saved addresses, optionally narrowed by the search term.
    /// </summary>
    /// <remarks>
    /// The match is a case-insensitive substring across the label and all three lines, because a
    /// passenger typing "off" means their office and a passenger typing "colom" means the line that
    /// says Colombo. <c>is_home</c>/<c>is_work</c> sort first: those two are the shortcuts US-7.13
    /// promises at the top of the list.
    /// </remarks>
    private const string SavedSql =
        """
        SELECT ST_Y(geo::geometry) AS Lat,
               ST_X(geo::geometry) AS Lng,
               label               AS Label,
               line1               AS Line1,
               line2               AS Line2,
               line3               AS Line3
          FROM iam.saved_addresses
         WHERE user_id = @UserId
           AND (@Term IS NULL
             OR label ILIKE @Pattern
             OR line1 ILIKE @Pattern
             OR COALESCE(line2, '') ILIKE @Pattern
             OR COALESCE(line3, '') ILIKE @Pattern)
         ORDER BY (is_home OR is_work) DESC, created_at DESC
         LIMIT @Limit;
        """;

    /// <summary>
    /// Distinct recent drop-offs.
    /// </summary>
    /// <remarks>
    /// Rounded to four decimals (~11 m) before the distinct, so ten rides to the same office door do
    /// not fill the list with ten entries that differ by GNSS noise. The label is left null: a stored
    /// coordinate has no address, and reverse-geocoding a whole list on the search path would put N
    /// Nominatim calls behind every keystroke.
    /// </remarks>
    private const string RecentSql =
        """
        SELECT DISTINCT ON (Lat, Lng) Lat, Lng, LastUsedAt
          FROM (
            SELECT round(ST_Y(dropoff_geo::geometry)::numeric, 4)::float8 AS Lat,
                   round(ST_X(dropoff_geo::geometry)::numeric, 4)::float8 AS Lng,
                   created_at                                             AS LastUsedAt
              FROM rides.rides
             WHERE (passenger_id = @UserId OR booker_id = @UserId OR rider_id = @UserId)
             ORDER BY created_at DESC
             LIMIT 200
          ) recent
         ORDER BY Lat, Lng, LastUsedAt DESC;
        """;

    public async Task<IReadOnlyList<KnownPlace>> SavedAsync(
        Guid userId, string? term, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(ReadConsistency.Eventual, cancellationToken);

        var rows = await connection.QueryAsync<SavedRow>(
            new CommandDefinition(
                SavedSql,
                new
                {
                    UserId = userId,
                    Term = string.IsNullOrWhiteSpace(term) ? null : term,
                    Pattern = string.IsNullOrWhiteSpace(term) ? null : $"%{term.Trim()}%",
                    Limit = limit,
                },
                cancellationToken: cancellationToken));

        return [.. rows.Select(static row => row.ToPlace())];
    }

    public async Task<IReadOnlyList<KnownPlace>> RecentAsync(
        Guid userId, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(ReadConsistency.Eventual, cancellationToken);

        var rows = await connection.QueryAsync<RecentRow>(
            new CommandDefinition(RecentSql, new { UserId = userId }, cancellationToken: cancellationToken));

        return
        [
            .. rows
                .OrderByDescending(static row => row.LastUsedAt)
                .Take(limit)
                .Select(static row => new KnownPlace(
                    new GeoPoint(row.Lat, row.Lng),
                    // No stored address — see the remarks on RecentSql. The coordinate is the answer;
                    // the client already renders a recent entry with its own affordance.
                    string.Empty,
                    null,
                    null,
                    PlaceSources.Recent,
                    null)),
        ];
    }

    private sealed record SavedRow(
        double Lat, double Lng, string Label, string Line1, string? Line2, string? Line3)
    {
        internal KnownPlace ToPlace()
        {
            var lines = new[] { Line1, Line2, Line3 }
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            return new KnownPlace(
                new GeoPoint(Lat, Lng),
                string.Join(", ", lines),
                Line1,
                Line3 ?? Line2,
                PlaceSources.Saved,
                Label);
        }
    }

    private sealed record RecentRow(double Lat, double Lng, DateTimeOffset LastUsedAt);
}
