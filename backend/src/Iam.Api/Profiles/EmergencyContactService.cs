using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.Iam.Profiles;

/// <summary><c>iam.yaml#/components/schemas/EmergencyContactInput</c> as a command.</summary>
public sealed record EmergencyContactCommand(string? Name, string? Phone);

/// <summary>
/// The driver SOS contact list (AL-13) and the denormalised primary <c>POST /v1/sos</c> reads.
/// </summary>
public interface IEmergencyContactService
{
    Task<IReadOnlyList<EmergencyContact>> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task<EmergencyContact> CreateAsync(Guid userId, EmergencyContactCommand command, CancellationToken cancellationToken);

    Task<EmergencyContact> UpdateAsync(
        Guid userId, Guid contactId, EmergencyContactCommand command, CancellationToken cancellationToken);

    Task DeleteAsync(Guid userId, Guid contactId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IEmergencyContactService"/>
/// <remarks>
/// Every mutation re-derives the primary and rewrites <c>iam.users.emergency_contact_name</c> /
/// <c>.emergency_contact_phone</c> <b>inside the same transaction</b>. Two copies of one fact is
/// what D-33's five-second SOS budget buys — safety-svc reads two flat columns rather than
/// joining — and the only way that stays safe is if they can never be observed disagreeing.
/// </remarks>
public sealed class EmergencyContactService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    IEmergencyContactRepository contacts) : IEmergencyContactService
{
    private const int MaxNameLength = 120;

    /// <summary>
    /// How many SOS contacts one account may keep.
    /// </summary>
    /// <remarks>
    /// No spec names a number. D-33 gives the whole SOS fan-out five seconds and safety-svc
    /// messages every contact, so the list is a latency budget as much as a preference; five is
    /// generous for "family and a friend" and keeps the fan-out inside it.
    /// </remarks>
    private const int MaxContactsPerUser = 5;

    public async Task<IReadOnlyList<EmergencyContact>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await contacts.ListAsync(connection, null, userId, cancellationToken);
    }

    public async Task<EmergencyContact> CreateAsync(
        Guid userId, EmergencyContactCommand command, CancellationToken cancellationToken)
    {
        var (name, phone) = Validate(command);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await contacts.ListAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken);

        if (existing.Count >= MaxContactsPerUser)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"An account may keep at most {MaxContactsPerUser} emergency contacts (D-33 fan-out budget).");
        }

        if (existing.Any(contact => string.Equals(contact.Phone, phone, StringComparison.Ordinal)))
        {
            // Two rows with one number means safety-svc SMSes the same person twice on an SOS.
            throw new MageRideException(MageRideErrors.Conflict, "That number is already an emergency contact.");
        }

        var created = await contacts.InsertAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, name, phone, cancellationToken);

        var primary = await SyncPrimaryAsync(unitOfWork, userId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return created with { IsPrimary = primary?.Id == created.Id };
    }

    public async Task<EmergencyContact> UpdateAsync(
        Guid userId, Guid contactId, EmergencyContactCommand command, CancellationToken cancellationToken)
    {
        var (name, phone) = Validate(command);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var updated = await contacts.UpdateAsync(
                          unitOfWork.Connection, unitOfWork.Transaction, userId, contactId, name, phone, cancellationToken)
                      ?? throw NotFound(contactId);

        var primary = await SyncPrimaryAsync(unitOfWork, userId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return updated with { IsPrimary = primary?.Id == updated.Id };
    }

    public async Task DeleteAsync(Guid userId, Guid contactId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        if (!await contacts.DeleteAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, contactId, cancellationToken))
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw NotFound(contactId);
        }

        // Deleting the primary promotes the next; deleting the last clears both columns and puts
        // POST /v1/sos back to 400 no-emergency-contact, which is the correct state for a driver
        // who has removed everybody.
        await SyncPrimaryAsync(unitOfWork, userId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task<EmergencyContact?> SyncPrimaryAsync(
        IUnitOfWork unitOfWork, Guid userId, CancellationToken cancellationToken)
    {
        var current = await contacts.ListAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken);
        var primary = current.Count > 0 ? current[0] : null;

        await contacts.SetPrimaryAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, primary?.Name, primary?.Phone, cancellationToken);

        return primary;
    }

    private static (string Name, string Phone) Validate(EmergencyContactCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = command.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors["name"] = ["name is required."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = [$"name must be at most {MaxNameLength} characters."];
        }

        // Normalised, not merely pattern-checked: an SOS dials this number, so "077 123 4567" has
        // to become +94771234567 here rather than at three o'clock in the morning in safety-svc.
        if (!PhoneNumbers.TryNormalise(command.Phone, out var phone))
        {
            errors["phone"] = ["phone must be a Sri Lankan mobile number in E.164 (+947XXXXXXXX)."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        return (name!, phone);
    }

    private static MageRideException NotFound(Guid contactId) =>
        new(MageRideErrors.NotFound, $"No emergency contact '{contactId}'.");
}
