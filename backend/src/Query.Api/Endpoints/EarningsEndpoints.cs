using System.Globalization;
using MageRide.Query.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Query.Endpoints;

/// <summary>The three periods D3' allows on the earnings dashboard.</summary>
/// <remarks>
/// Resolved in <b>Asia/Colombo</b> (D-13, D-38), which is the whole reason they are resolved here and
/// not left to the client: a driver's "today" ends at midnight local, and computing it from a UTC clock
/// puts the first five and a half hours of every Colombo day in the previous one. "Week" is
/// Monday-to-today and "month" is the 1st-to-today rather than rolling windows — a driver comparing
/// their app against their own week means the calendar one.
/// </remarks>
public static class EarningsPeriods
{
    public const string Today = "today";
    public const string Week = "week";
    public const string Month = "month";

    /// <summary>The inclusive Colombo date range a period covers.</summary>
    public static (DateOnly From, DateOnly To) Resolve(string period, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var today = BusinessCalendar.Today(clock);

        return period switch
        {
            Today => (today, today),

            // DayOfWeek.Monday is 1 and Sunday is 0, so Sunday has to be treated as day seven or a
            // Sunday's "this week" would be the week about to start.
            Week => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today),

            Month => (new DateOnly(today.Year, today.Month, 1), today),

            _ => throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["period"] = ["period must be one of today, week or month."],
            }),
        };
    }
}

/// <summary>
/// <c>/v1/earnings</c> — the driver earnings dashboard (US-9.22) and its per-ride breakdown.
/// </summary>
/// <remarks>
/// <b>A driver may read their own earnings, and the back office may read anyone's.</b> The
/// <c>{driverId}</c> in the path goes through the same <see cref="SubjectScope"/> check the trips
/// routes use. There is deliberately no fleet-owner path here: a Mode A/B fleet's vehicles earn no
/// per-ride fare (Mode A is free to ride, Mode B is a monthly subscription paid to the owner,
/// BR-23.8/23.9), so a fleet owner asking this endpoint about their driver would be asking the wrong
/// question — their money is <c>subscription.*</c>, which is fleet-svc's (C059).
/// </remarks>
public static class EarningsEndpoints
{
    public static IEndpointRouteBuilder MapEarningsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var earnings = endpoints.MapGroup("/v1/earnings").WithTags("earnings").RequireAuthorization();

        earnings.MapGet("/{driverId}", SummaryAsync).WithName("getDriverEarnings");
        earnings.MapGet("/{driverId}/sessions", SessionsAsync).WithName("listEarningSessions");

        return endpoints;
    }

    private static async Task<Ok<EarningsSummaryResponse>> SummaryAsync(
        string driverId,
        string? period,
        HttpContext context,
        IEarningsRepository repository,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);

        var driver = SubjectScope.Require(context.User, driverId);
        var resolved = string.IsNullOrWhiteSpace(period) ? EarningsPeriods.Today : period;
        var (from, to) = EarningsPeriods.Resolve(resolved, clock);

        var totals = await repository.TotalsAsync(driver, from, to, cancellationToken);

        return TypedResults.Ok(EarningsSummaryResponse.From(resolved, from, to, totals));
    }

    /// <summary>
    /// One row per earning ride, newest first.
    /// </summary>
    /// <remarks>
    /// <c>from</c>/<c>to</c> are Asia/Colombo business dates, matching the column they filter and the
    /// summary above. Absent, the range is the current month — the same window the dashboard's
    /// <c>period=month</c> shows, so opening the breakdown from the dashboard does not silently change
    /// the question.
    /// </remarks>
    private static async Task<Ok<CursorPage<SessionEarningResponse>>> SessionsAsync(
        string driverId,
        string? from,
        string? to,
        HttpContext context,
        IEarningsRepository repository,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);

        var driver = SubjectScope.Require(context.User, driverId);
        var (defaultFrom, defaultTo) = EarningsPeriods.Resolve(EarningsPeriods.Month, clock);

        var rangeFrom = ParseDate(from, "from") ?? defaultFrom;
        var rangeTo = ParseDate(to, "to") ?? defaultTo;

        if (rangeFrom > rangeTo)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["from"] = ["from must not be after to."],
            });
        }

        var page = PageRequest.FromQuery(context.Request);
        var (before, beforeId) = SessionEarningCursor.Decode(page.Cursor);

        var rows = await repository.SessionsAsync(
            driver, rangeFrom, rangeTo, before, beforeId, page.OverfetchLimit, cancellationToken);

        var mapped = rows.Select(SessionEarningResponse.From).ToArray();

        return TypedResults.Ok(
            CursorPage<SessionEarningResponse>.FromOverfetch(
                mapped, page.Limit, SessionEarningCursor.Encode));
    }

    private static DateOnly? ParseDate(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} must be an Asia/Colombo business date in yyyy-MM-dd form."],
            });
        }

        return parsed;
    }
}

/// <summary>Keyset cursor over <c>(endedAt, tripId)</c>, for the same reason <see cref="TripCursor"/> is.</summary>
internal static class SessionEarningCursor
{
    private const char Separator = '|';

    internal static string Encode(SessionEarningResponse last)
    {
        ArgumentNullException.ThrowIfNull(last);

        return CursorCodec.Unsigned.EncodeString(
            string.Create(CultureInfo.InvariantCulture, $"{last.EndedAt.UtcDateTime:O}{Separator}{last.TripId}"));
    }

    internal static (DateTimeOffset? Before, Guid? BeforeId) Decode(string? cursor)
    {
        if (!CursorCodec.Unsigned.TryDecodeString(cursor, out var raw))
        {
            return (null, null);
        }

        var parts = raw.Split(Separator);

        if (parts.Length != 2
            || !DateTimeOffset.TryParse(
                parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var before)
            || !Guid.TryParse(parts[1], out var id))
        {
            return (null, null);
        }

        return (before, id);
    }
}
