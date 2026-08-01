using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Endpoints;
using MageRide.AdminBff.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.AdminBff.Platform;

/// <summary>Train registration and lifecycle — admin-only Mode A (US-2.17/2.18).</summary>
public interface ITrainService
{
    Task<TrainResponse> CreateAsync(TrainBody? body, Guid actorId, CancellationToken cancellationToken);

    Task<TrainResponse> UpdateAsync(Guid trainId, TrainBody? body, Guid actorId, CancellationToken cancellationToken);

    Task RetireAsync(Guid trainId, Guid actorId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrainService"/>
internal sealed class TrainService(
    IUnitOfWorkFactory unitOfWorkFactory,
    ITrainRepository trains,
    IAdminAuditContext audit,
    ILogger<TrainService> logger) : ITrainService
{
    public async Task<TrainResponse> CreateAsync(
        TrainBody? body, Guid actorId, CancellationToken cancellationToken)
    {
        var input = Validate(body);
        var trainId = Guid.CreateVersion7();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var created = await trains.InsertAsync(
            unitOfWork,
            trainId,
            // The registering admin owns the row. registry.vehicles.owner_id is NOT NULL and points
            // at a real account; see TrainRepository's remark for why a synthetic platform user
            // would be worse.
            ownerId: actorId,
            input.Name,
            input.TrainNumber,
            input.RouteId,
            input.Active,
            cancellationToken);

        if (created is null)
        {
            throw new MageRideException(
                MageRideErrors.RegistrationExists,
                $"'{input.TrainNumber}' is already carried by a live registration (D-37).");
        }

        audit.Record(trainId, after: created);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Train {TrainId} ({TrainNumber}) registered by {ActorId}.", trainId, input.TrainNumber, actorId);

        return ToResponse(created);
    }

    public async Task<TrainResponse> UpdateAsync(
        Guid trainId, TrainBody? body, Guid actorId, CancellationToken cancellationToken)
    {
        var input = Validate(body);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await trains.ReadAsync(unitOfWork, trainId, cancellationToken)
                     ?? throw new MageRideException(MageRideErrors.NotFound, "No such train.");

        var after = await trains.UpdateAsync(
            unitOfWork, trainId, input.Name, input.TrainNumber, input.RouteId, input.Active, cancellationToken);

        audit.Record(trainId, before: before, after: after);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Train {TrainId} updated by {ActorId}.", trainId, actorId);

        return ToResponse(after);
    }

    public async Task RetireAsync(Guid trainId, Guid actorId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await trains.ReadAsync(unitOfWork, trainId, cancellationToken)
                     ?? throw new MageRideException(MageRideErrors.NotFound, "No such train.");

        await trains.RetireAsync(unitOfWork, trainId, cancellationToken);

        // A retirement's after-image is the state, not an absence: `active: false` says the row is
        // still there and still resolvable from every historical trip, which is what "soft-retires"
        // means. A null after would read as a delete.
        audit.Record(trainId, before: before, after: before with { Active = false });

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Train {TrainId} ({TrainNumber}) retired by {ActorId}; historical trips keep their reference.",
            trainId, before.TrainNumber, actorId);
    }

    private static (string Name, string TrainNumber, Guid? RouteId, bool Active) Validate(TrainBody? body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = body?.Name?.Trim();
        var number = body?.TrainNumber?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            errors["name"] = ["name is required and is at most 200 characters — it is what passengers see."];
        }

        if (string.IsNullOrEmpty(number) || number.Length > 32)
        {
            errors["trainNumber"] = ["trainNumber is required and is at most 32 characters."];
        }

        Guid? routeId = null;

        if (body?.RouteId is { Length: > 0 } route)
        {
            if (Guid.TryParse(route, out var parsed))
            {
                routeId = parsed;
            }
            else
            {
                errors["routeId"] = ["routeId must be a ULID/UUID naming a spatial.routes line."];
            }
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        return (name!, number!, routeId, body?.Active ?? true);
    }

    private static TrainResponse ToResponse(Domain.Train train) =>
        new(train.TrainId, train.Name, train.TrainNumber, train.RouteId, train.Active);
}
