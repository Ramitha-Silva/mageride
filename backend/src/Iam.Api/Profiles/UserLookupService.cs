using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace MageRide.Iam.Profiles;

/// <summary>The whole answer <c>GET /v1/users/lookup</c> gives (P-03).</summary>
public sealed record PhoneLookupResult(bool Registered, Guid? UserId);

/// <summary>
/// The proxy-booking rider lookup (P-03): does this number belong to a MageRide account?
/// </summary>
public interface IUserLookupService
{
    Task<PhoneLookupResult> LookupAsync(string? phone, string? caller, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IUserLookupService"/>
/// <remarks>
/// <para>
/// ride-svc calls this to choose between an in-app FCM location request and the SMS
/// <c>pickup_confirm</c> web path (AL-45), so the answer decides which of two flows a stranger's
/// phone number is put through. It returns <b>nothing beyond the answer</b>: no name, no photo,
/// no phone echo. Widening it later is the easy mistake — a booker who mistypes a digit would
/// learn the name of whoever owns the number they reached.
/// </para>
/// <para>
/// The lookup is recorded in <c>iam.phone_lookups</c> as a keyed HMAC and never as a number
/// (P-03, migration 0108). The write is best-effort: an audit row that cannot be written must not
/// take down a booking, and the booking is what the passenger is waiting for.
/// </para>
/// </remarks>
public sealed class UserLookupService(
    INpgsqlConnectionFactory connections,
    IUserRepository users,
    IPhoneLookupRepository lookups,
    PhoneHasher phones,
    ILogger<UserLookupService> logger) : IUserLookupService
{
    public async Task<PhoneLookupResult> LookupAsync(
        string? phone, string? caller, CancellationToken cancellationToken)
    {
        if (!PhoneNumbers.TryNormalise(phone, out var normalised))
        {
            throw new MageRideException(
                MageRideErrors.InvalidPhone, "phone must be a Sri Lankan mobile number in E.164 (+947XXXXXXXX).");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        var user = await users.FindByPhoneAsync(connection, null, normalised, cancellationToken);

        // A blocked account is still a registered one. Answering false would send a proxy rider
        // down the unregistered SMS path, where nothing checks the block either — and would tell
        // the caller something about the account's standing that this endpoint has no business
        // disclosing.
        var result = new PhoneLookupResult(user is not null, user?.Id);

        try
        {
            await lookups.RecordAsync(
                connection, phones.Hash(normalised), result.Registered, result.UserId, caller, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record a phone lookup in iam.phone_lookups; the answer was still given");
        }

        return result;
    }
}
