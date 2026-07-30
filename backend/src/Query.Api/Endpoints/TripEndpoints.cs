using System.Globalization;
using MageRide.Query.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Query.Endpoints;

/// <summary>
/// <c>/v1/trips</c> — history and detail (US-8.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>{userId}</c> in the path is checked against the token, never trusted.</b> D3' spells the
/// route with the id in it and every client sends its own; a caller asking for somebody else's history
/// is <c>403</c>. The six back-office roles are allowed through (US-24.9/24.10 give Support and Admin a
/// read-only trips tab on a passenger's record) and that read is what admin-bff's audit interceptor
/// stamps as a <c>PII_READ</c> — this service does not decide who may look, it enforces that a
/// passenger token may only look at itself.
/// </para>
/// <para>
/// <b>A trip that is not the caller's is <c>404</c>, not <c>403</c>.</b> The scoping is inside the
/// query — <c>WHERE id = @TripId AND (passenger_id = @UserId OR …)</c> — so "does not exist" and "is
/// not yours" are the same result, and telling them apart would be a membership oracle over other
/// people's journeys. trip-state-svc's <c>/active</c> is under the same rule for the same reason.
/// </para>
/// </remarks>
public static class TripEndpoints
{
    public static IEndpointRouteBuilder MapTripEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var trips = endpoints.MapGroup("/v1/trips").WithTags("trips").RequireAuthorization();

        trips.MapGet("/{userId}", ListAsync).WithName("listTrips");
        trips.MapGet("/{userId}/{tripId}", GetAsync).WithName("getTrip");

        return endpoints;
    }

    private static async Task<Ok<CursorPage<TripSummaryResponse>>> ListAsync(
        string userId,
        HttpContext context,
        ITripRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);

        var subject = SubjectScope.Require(context.User, userId);
        var page = PageRequest.FromQuery(context.Request);

        var (before, beforeId) = TripCursor.Decode(page.Cursor);

        var rows = await repository.ListAsync(
            subject, before, beforeId, page.OverfetchLimit, cancellationToken);

        var mapped = rows.Select(TripSummaryResponse.From).ToArray();

        return TypedResults.Ok(
            CursorPage<TripSummaryResponse>.FromOverfetch(mapped, page.Limit, TripCursor.Encode));
    }

    private static async Task<Ok<TripDetailResponse>> GetAsync(
        string userId,
        string tripId,
        HttpContext context,
        ITripRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(repository);

        var subject = SubjectScope.Require(context.User, userId);

        if (!Guid.TryParse(tripId, out var trip))
        {
            throw new MageRideException(MageRideErrors.NotFound, "No such trip.");
        }

        var detail = await repository.GetAsync(subject, trip, cancellationToken)
                     ?? throw new MageRideException(MageRideErrors.NotFound, "No such trip.");

        return TypedResults.Ok(TripDetailResponse.From(detail));
    }
}

/// <summary>
/// The opaque <c>cursor</c> for a keyset-paginated trip list.
/// </summary>
/// <remarks>
/// <para>
/// <c>(startedAt, tripId)</c>, because two trips can start in the same microsecond — a fleet's morning
/// departure does exactly that — and a cursor on the timestamp alone would skip rows or repeat them.
/// </para>
/// <para>
/// <b>Unsigned, and it does not matter.</b> A forged cursor moves the caller's own window through the
/// caller's own trips: the query is scoped by <c>@UserId</c> from the token and the cursor contributes
/// only an ordering bound, exactly as <c>CursorCodec</c>'s own remarks require. An unparseable cursor
/// is treated as the first page rather than a <c>400</c> — a client that upgraded across a cursor format
/// change should see the top of the list, not an error it cannot clear.
/// </para>
/// </remarks>
internal static class TripCursor
{
    private const char Separator = '|';

    internal static string Encode(TripSummaryResponse last)
    {
        ArgumentNullException.ThrowIfNull(last);

        return CursorCodec.Unsigned.EncodeString(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{last.StartedAt.UtcDateTime:O}{Separator}{last.TripId}"));
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

/// <summary>
/// Resolves the <c>{userId}</c> path parameter against the caller's token.
/// </summary>
/// <remarks>
/// One place, because the rule has to be identical on the four routes that carry an id in the path. A
/// per-endpoint check is how one of them ends up missing it, and the missing one is not visible from
/// the outside until somebody reads somebody else's trips.
/// </remarks>
internal static class SubjectScope
{
    /// <summary>
    /// The user whose data may be read, or a throw.
    /// </summary>
    /// <returns>
    /// The requested id when the caller is that user or holds a back-office role; the request is
    /// refused otherwise.
    /// </returns>
    internal static Guid Require(System.Security.Claims.ClaimsPrincipal? principal, string requestedUserId)
    {
        var caller = principal.RequireSubjectId();

        if (!Guid.TryParse(requestedUserId, out var requested))
        {
            // A malformed id in the path is not a validation error to report in detail: answering
            // "that is not a ULID" for a value that is not the caller's own id anyway tells a prober
            // which of their guesses are well formed.
            throw new MageRideException(MageRideErrors.Forbidden, "This resource is not yours.");
        }

        if (requested == caller)
        {
            return requested;
        }

        // AL-02/AL-06: Support, Admin and the other four back-office roles read a user's record from
        // the Admin Portal (US-24.9/24.10). The audit trail for that read is admin-bff's PII_READ
        // interceptor (D-35), not this service's — but the read itself has to be possible.
        var roles = principal.Roles();

        if (roles.Any(MageRideRoles.Internal.Contains))
        {
            return requested;
        }

        throw new MageRideException(MageRideErrors.Forbidden, "This resource is not yours.");
    }
}
