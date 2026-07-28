using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.TripState.Domain;
using MageRide.TripState.Persistence;

namespace MageRide.TripState.Sessions;

/// <summary>One direction of a journey rating (US-18.1, US-18.2, US-8.6).</summary>
/// <param name="RaterId">Who is rating — always the authenticated caller, never the body.</param>
/// <param name="Counterparty">The passenger, on the driver's side. Null on the passenger's side,
/// where the ratee is whoever drove.</param>
public sealed record RateSessionCommand(
    Guid RaterId, string? SessionId, int? Stars, string? Text, string? Counterparty, string Direction);

/// <summary>Journey ratings, both ways.</summary>
public interface IRatingService
{
    Task<SessionRating> RateAsync(RateSessionCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRatingService"/>
public sealed class RatingService(
    IUnitOfWorkFactory unitOfWorkFactory,
    ISessionRepository sessions,
    IRatingRepository ratings) : IRatingService
{
    /// <summary>D3' and the contract: 1–5 stars, and <c>trips.ratings</c> CHECKs the same range.</summary>
    private const int MinStars = 1;
    private const int MaxStars = 5;

    public async Task<SessionRating> RateAsync(RateSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var request = Validate(command);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var session = await sessions.FindAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, request.SessionId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.NotFound, $"No session {request.SessionId}.");

        // Who is being rated depends on the direction, and only one of the two is on the wire. The
        // driver comes from the session — a passenger cannot name who they are rating, because the
        // session is the only thing that knows who was driving.
        var (raterId, rateeId) = command.Direction == RatingDirections.PassengerToDriver
            ? (command.RaterId, session.DriverId)
            : (session.DriverId, request.Counterparty!.Value);

        // A driver rating a passenger must actually be the driver of that session. A passenger
        // needs no such check: Mode A is a public bus and this service holds no manifest, so
        // "was this person aboard" is a question it cannot answer and must not pretend to.
        if (command.Direction == RatingDirections.DriverToPassenger && session.DriverId != command.RaterId)
        {
            throw new MageRideException(MageRideErrors.Forbidden, "Only the driver of a session may rate its passengers.");
        }

        // Rating yourself is a client bug, and the reputation counters it feeds (D5' §4.1) would
        // take it seriously.
        if (raterId == rateeId)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["passengerId"] = ["A session cannot be rated by the person being rated."],
            });
        }

        var rating = await ratings.InsertAsync(
                         unitOfWork.Connection,
                         unitOfWork.Transaction,
                         session.Id,
                         raterId,
                         rateeId,
                         (short)request.Stars,
                         request.Text,
                         command.Direction,
                         cancellationToken)
                     ?? throw new MageRideException(
                         MageRideErrors.Conflict, "You have already rated this journey.");

        await unitOfWork.CommitAsync(cancellationToken);

        return rating;
    }

    private static RatingRequest Validate(RateSessionCommand command)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!Guid.TryParse(command.SessionId, out var sessionId))
        {
            // A malformed path segment is 404 on the way in; here it is a field, because the
            // session id reaching this method has already been through the route.
            throw new MageRideException(MageRideErrors.NotFound, "No such session.");
        }

        if (command.Stars is not { } stars || stars is < MinStars or > MaxStars)
        {
            errors["stars"] = [$"stars must be between {MinStars} and {MaxStars}."];
        }

        Guid? counterparty = null;

        if (command.Direction == RatingDirections.DriverToPassenger)
        {
            if (Guid.TryParse(command.Counterparty, out var parsed) && parsed != Guid.Empty)
            {
                counterparty = parsed;
            }
            else
            {
                errors["passengerId"] = ["passengerId is required and must be an identifier."];
            }
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        return new RatingRequest(sessionId, command.Stars!.Value, command.Text, counterparty);
    }

    private sealed record RatingRequest(Guid SessionId, int Stars, string? Text, Guid? Counterparty);
}
