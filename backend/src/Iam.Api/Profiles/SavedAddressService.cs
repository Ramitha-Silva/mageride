using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Iam.Profiles;

/// <summary><c>iam.yaml#/components/schemas/SavedAddressInput</c> as a command.</summary>
public sealed record SavedAddressCommand(
    string? Label,
    string? Line1,
    string? Line2,
    string? Line3,
    double? Lat,
    double? Lng,
    bool IsHome,
    bool IsWork);

/// <summary>Home, Work and free-form saved places (AL-14, AL-26, US-22.1/22.2).</summary>
public interface ISavedAddressService
{
    Task<IReadOnlyList<SavedAddress>> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task<SavedAddress> CreateAsync(Guid userId, SavedAddressCommand command, CancellationToken cancellationToken);

    Task<SavedAddress> UpdateAsync(
        Guid userId, Guid addressId, SavedAddressCommand command, CancellationToken cancellationToken);

    Task DeleteAsync(Guid userId, Guid addressId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISavedAddressService"/>
public sealed class SavedAddressService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    ISavedAddressRepository addresses) : ISavedAddressService
{
    private const int MaxLabelLength = 60;
    private const int MaxLineLength = 200;

    /// <summary>
    /// How many places one account may save.
    /// </summary>
    /// <remarks>
    /// No spec names a number. The list is part of the bounded eager-fetch payload (NFR-51), so
    /// it needs *a* ceiling or a login payload grows without limit; fifty is far past what
    /// D2 SCR-PA-026's scrolling list is for and low enough that the bootstrap response stays
    /// small on a mobile connection.
    /// </remarks>
    private const int MaxAddressesPerUser = 50;

    public async Task<IReadOnlyList<SavedAddress>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await addresses.ListAsync(connection, null, userId, cancellationToken);
    }

    public async Task<SavedAddress> CreateAsync(
        Guid userId, SavedAddressCommand command, CancellationToken cancellationToken)
    {
        var draft = Validate(command, userId, Guid.Empty);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var existing = await addresses.ListAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken);

        if (existing.Count >= MaxAddressesPerUser)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"An account may save at most {MaxAddressesPerUser} addresses.");
        }

        // Moving Home or Work onto this address is an edit, not a collision — the contract says
        // so for PUT and the same is true of a POST that claims a flag somebody's older address
        // holds. The partial unique indexes stay the enforcement; this is what keeps them from
        // firing on the ordinary case.
        await addresses.ClearFlagsAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, null, draft.IsHome, draft.IsWork, cancellationToken);

        SavedAddress saved;
        try
        {
            saved = await addresses.InsertAsync(
                unitOfWork.Connection, unitOfWork.Transaction, draft, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Two requests claiming Home at once: one clears the old flag, one inserts, and the
            // loser meets uq_saved_home. A 409 is the honest answer — the client's idea of which
            // address is Home is already stale.
            throw new MageRideException(
                MageRideErrors.Conflict, "Another request is already setting this Home or Work address.");
        }

        await unitOfWork.CommitAsync(cancellationToken);

        return saved;
    }

    public async Task<SavedAddress> UpdateAsync(
        Guid userId, Guid addressId, SavedAddressCommand command, CancellationToken cancellationToken)
    {
        var draft = Validate(command, userId, addressId);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        _ = await addresses.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, addressId, cancellationToken)
            ?? throw NotFound(addressId);

        await addresses.ClearFlagsAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            userId,
            addressId,
            draft.IsHome,
            draft.IsWork,
            cancellationToken);

        var updated = await addresses.UpdateAsync(unitOfWork.Connection, unitOfWork.Transaction, draft, cancellationToken)
                      ?? throw NotFound(addressId);

        await unitOfWork.CommitAsync(cancellationToken);

        return updated;
    }

    public async Task DeleteAsync(Guid userId, Guid addressId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        if (!await addresses.DeleteAsync(
                unitOfWork.Connection, unitOfWork.Transaction, userId, addressId, cancellationToken))
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            throw NotFound(addressId);
        }

        await unitOfWork.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Validates the body and reconciles the two spellings of Home and Work.
    /// </summary>
    /// <remarks>
    /// <c>label</c> and <c>isHome</c>/<c>isWork</c> both exist and both mean something (see
    /// <see cref="SavedAddress"/>). They are reconciled rather than fought over: a body labelled
    /// <c>home</c> sets <c>is_home</c>, and a body with <c>isHome</c> is labelled <c>home</c>.
    /// The one combination that cannot be honoured — <c>{label:"work", isHome:true}</c> — is a
    /// 400 rather than a guess, because either reading silently discards half of what was asked.
    /// </remarks>
    private static SavedAddress Validate(SavedAddressCommand command, Guid userId, Guid addressId)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var label = command.Label?.Trim();
        if (string.IsNullOrEmpty(label))
        {
            errors["label"] = ["label is required (\"Save Address As\", AL-26)."];
        }
        else if (label.Length > MaxLabelLength)
        {
            errors["label"] = [$"label must be at most {MaxLabelLength} characters."];
        }

        var line1 = command.Line1?.Trim();
        if (string.IsNullOrEmpty(line1))
        {
            errors["line1"] = ["line1 is required (Address Line 1, AL-26)."];
        }
        else if (line1.Length > MaxLineLength)
        {
            errors["line1"] = [$"line1 must be at most {MaxLineLength} characters."];
        }

        var line2 = Line(command.Line2, "line2", errors);
        var line3 = Line(command.Line3, "line3", errors);

        if (command.Lat is not { } lat || lat is < -90 or > 90)
        {
            errors["lat"] = ["lat is required and must be between -90 and 90."];
        }

        if (command.Lng is not { } lng || lng is < -180 or > 180)
        {
            errors["lng"] = ["lng is required and must be between -180 and 180."];
        }

        if (command.IsHome && command.IsWork)
        {
            errors["isHome"] = ["An address is Home or Work, not both (ck_saved_addr_home_work)."];
        }

        var isHome = command.IsHome || SavedAddressLabels.Home.Equals(label, StringComparison.OrdinalIgnoreCase);
        var isWork = command.IsWork || SavedAddressLabels.Work.Equals(label, StringComparison.OrdinalIgnoreCase);

        if (isHome && isWork)
        {
            errors["label"] =
                ["The label and the isHome/isWork flags disagree; an address is Home or Work, not both."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        // A flag set without a reserved label adopts one, so the list a client renders from
        // labels and the invariant the indexes enforce cannot describe different addresses.
        if (isHome && !SavedAddressLabels.IsReserved(label))
        {
            label = SavedAddressLabels.Home;
        }
        else if (isWork && !SavedAddressLabels.IsReserved(label))
        {
            label = SavedAddressLabels.Work;
        }

        var now = DateTimeOffset.UtcNow;

        return new SavedAddress(
            addressId,
            userId,
            label!,
            line1!,
            line2,
            line3,
            new GeoPoint(command.Lat!.Value, command.Lng!.Value),
            isHome,
            isWork,
            now,
            now);
    }

    private static string? Line(string? value, string field, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxLineLength)
        {
            errors[field] = [$"{field} must be at most {MaxLineLength} characters."];
        }

        return trimmed;
    }

    private static MageRideException NotFound(Guid addressId) =>
        new(MageRideErrors.NotFound, $"No saved address '{addressId}'.");
}
