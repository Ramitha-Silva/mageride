using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Iam.Rbac;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.Iam.Profiles;

/// <summary>A profile plus the role facts every response about it carries.</summary>
public sealed record ProfileView(UserProfile Profile, IReadOnlyList<string> Roles, FleetMembership? Fleet);

/// <summary>The <c>PUT /v1/users/me</c> patch. Every field is optional (US-1.5).</summary>
public sealed record UpdateProfileCommand(
    string? FirstName,
    string? PhotoUrl,
    string? Language,
    IReadOnlyDictionary<string, bool>? NotifPrefs);

/// <summary>
/// <c>/v1/users/me</c> and the three preference routes — the profile surface both apps and both
/// portals read (D2 SCR-PA/PI-027, 027b).
/// </summary>
public interface IProfileService
{
    Task<ProfileView> GetAsync(Guid userId, CancellationToken cancellationToken);

    Task<ProfileView> UpdateAsync(Guid userId, UpdateProfileCommand command, CancellationToken cancellationToken);

    Task<UserProfile> SetLanguageAsync(Guid userId, string? language, CancellationToken cancellationToken);

    Task<UserProfile> SetDefaultPaymentMethodAsync(Guid userId, string? method, CancellationToken cancellationToken);

    Task<UserProfile> SetOperatingCityAsync(Guid userId, string? cityCode, CancellationToken cancellationToken);

    /// <summary>The caller's effective permissions (AL-06).</summary>
    Task<EffectivePermissionSet> PermissionsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Records an erasure request and answers <c>202</c> (US-1.8, E-06). Fulfilment is C065's.
    /// </summary>
    Task<PdpaRequest> RequestErasureAsync(Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IProfileService"/>
public sealed class ProfileService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    IProfileRepository profiles,
    IUserRepository users,
    IPdpaRequestRepository pdpa,
    IPolicyEvaluator policies) : IProfileService
{
    /// <summary>The three languages every user-facing string exists in (D-26, CLAUDE.md).</summary>
    public static readonly IReadOnlySet<string> Languages =
        new HashSet<string>(StringComparer.Ordinal) { "si", "ta", "en" };

    /// <summary>The stored payment preference (AL-14, US-22.4). Settlement-time methods are not here.</summary>
    public static readonly IReadOnlySet<string> PaymentMethods =
        new HashSet<string>(StringComparer.Ordinal) { "cash", "lankaqr", "onepay" };

    /// <summary>
    /// Notification types that cannot be muted (US-10.7, notification.yaml). A body that tries is
    /// not an error — the switch simply does not exist on the server, and a client that draws one
    /// is showing a control that was never honoured.
    /// </summary>
    public static readonly IReadOnlySet<string> UnmutableNotifications =
        new HashSet<string>(StringComparer.Ordinal) { "SOS_TRIGGERED", "SOS_RESOLVED", "RIDE_CANCELLED" };

    private const int MaxFirstNameLength = 120;
    private const int MaxPhotoUrlLength = 2048;
    private const int MaxNotificationTypes = 64;

    public async Task<ProfileView> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var profile = await profiles.FindAsync(connection, null, userId, cancellationToken)
                      ?? throw Gone();

        var principal = await users.PrincipalAsync(connection, null, userId, cancellationToken);

        return new ProfileView(profile, principal.Roles, principal.Fleet);
    }

    public async Task<ProfileView> UpdateAsync(
        Guid userId, UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var firstName = Trimmed(command.FirstName);
        if (firstName is { Length: > MaxFirstNameLength })
        {
            errors["firstName"] = [$"firstName must be at most {MaxFirstNameLength} characters."];
        }

        var photoUrl = Trimmed(command.PhotoUrl);
        if (photoUrl is not null &&
            (photoUrl.Length > MaxPhotoUrlLength || !Uri.TryCreate(photoUrl, UriKind.Absolute, out _)))
        {
            errors["photoUrl"] = ["photoUrl must be an absolute URI."];
        }

        var language = Trimmed(command.Language);
        if (language is not null && !Languages.Contains(language))
        {
            errors["language"] = ["language must be one of si, ta, en (D-26)."];
        }

        var notifPrefs = command.NotifPrefs is null ? null : Sanitise(command.NotifPrefs, errors);

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // Read first so a request for an account that no longer exists is a 401 rather than a
        // Dapper "sequence contains no elements" out of the UPDATE ... RETURNING.
        _ = await profiles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken)
            ?? throw Gone();

        var updated = await profiles.UpdateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            userId,
            firstName,
            photoUrl,
            language,
            notifPrefs,
            cancellationToken);

        var principal = await users.PrincipalAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return new ProfileView(updated, principal.Roles, principal.Fleet);
    }

    public Task<UserProfile> SetLanguageAsync(Guid userId, string? language, CancellationToken cancellationToken)
    {
        var value = Trimmed(language);

        if (value is null || !Languages.Contains(value))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["language"] = ["language is required and must be one of si, ta, en (D-26)."],
            });
        }

        return SetAsync(userId, ProfilePreference.Language, value, cancellationToken);
    }

    public Task<UserProfile> SetDefaultPaymentMethodAsync(
        Guid userId, string? method, CancellationToken cancellationToken)
    {
        var value = Trimmed(method);

        if (value is null || !PaymentMethods.Contains(value))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["defaultPaymentMethod"] =
                    ["defaultPaymentMethod is required and must be one of cash, lankaqr, onepay (AL-14, US-22.4)."],
            });
        }

        return SetAsync(userId, ProfilePreference.DefaultPaymentMethod, value, cancellationToken);
    }

    public async Task<UserProfile> SetOperatingCityAsync(
        Guid userId, string? cityCode, CancellationToken cancellationToken)
    {
        var value = Trimmed(cityCode);

        if (value is null)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["operatingCityCode"] = ["operatingCityCode is required (AL-27, US-1.3a)."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // Checked rather than left to the foreign key: `config.operating_cities` has an `is_active`
        // flag the FK cannot see, so a city the platform has withdrawn would still satisfy the
        // constraint. A stale client list must not be able to put a user in a city nobody serves.
        if (!await profiles.IsOperatingCityActiveAsync(
                unitOfWork.Connection, unitOfWork.Transaction, value, cancellationToken))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["operatingCityCode"] = [$"'{value}' is not an active launch city (GET /v1/config/cities)."],
            });
        }

        var updated = await profiles.SetPreferenceAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            userId,
            ProfilePreference.OperatingCityCode,
            value,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return updated;
    }

    public async Task<EffectivePermissionSet> PermissionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // Resolved from the database, not from the caller's claims: this endpoint is what a
        // portal renders its menus from, and a session that predates a revocation would otherwise
        // draw a menu the API refuses.
        var principal = await users.PrincipalAsync(connection, null, userId, cancellationToken);

        return policies.Evaluate(userId, principal.Roles, principal.Fleet);
    }

    public async Task<PdpaRequest> RequestErasureAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        _ = await profiles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken)
            ?? throw Gone();

        // A second DELETE while one is open is a 409, not a second row. Two erasure requests for
        // one person are two 30-day clocks against one obligation, and whichever C065 fulfils
        // leaves the other permanently overdue in the SLA queue (ix_pdpa_requests_due).
        var open = await pdpa.FindOpenAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, PdpaRequestRepository.Erasure, cancellationToken);

        if (open is not null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.Conflict,
                $"An erasure request is already open for this account (due {open.DueBy:yyyy-MM-dd}).");
        }

        var request = await pdpa.InsertAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, PdpaRequestRepository.Erasure, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // Deliberately nothing else. The account is not blocked, no session is revoked and no
        // column is anonymised: erasure may be refused or held (FulfilledHold) and a user whose
        // request is rejected must find their account exactly as they left it (E-06). The fence
        // in the C027 prompt is the same sentence — iam only records the request.
        return request;
    }

    private async Task<UserProfile> SetAsync(
        Guid userId, ProfilePreference preference, string value, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        _ = await profiles.FindAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken)
            ?? throw Gone();

        var updated = await profiles.SetPreferenceAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, preference, value, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return updated;
    }

    /// <summary>
    /// Drops the switches US-10.7 does not offer and rejects a document that is too large to be a
    /// preference set.
    /// </summary>
    /// <remarks>
    /// Silently ignoring an <c>SOS_*</c> or <c>RIDE_CANCELLED</c> key is what
    /// <c>notification.yaml</c> specifies ("cannot be muted and are ignored if present"), and the
    /// two routes that write this column have to agree or the last writer wins with different
    /// rules.
    /// </remarks>
    private static Dictionary<string, bool> Sanitise(
        IReadOnlyDictionary<string, bool> requested, Dictionary<string, string[]> errors)
    {
        if (requested.Count > MaxNotificationTypes)
        {
            errors["notifPrefs"] = [$"notifPrefs may carry at most {MaxNotificationTypes} switches."];
            return [];
        }

        var sanitised = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var (type, enabled) in requested)
        {
            if (string.IsNullOrWhiteSpace(type) || UnmutableNotifications.Contains(type))
            {
                continue;
            }

            sanitised[type] = enabled;
        }

        return sanitised;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A valid token whose account is gone. 401, not 404: the caller's credential no longer
    /// identifies anybody, and answering 404 would describe the *account* as missing to a client
    /// that asked about itself.
    /// </summary>
    private static MageRideException Gone() =>
        new(MageRideErrors.Unauthorized, "The account behind this token no longer exists.");
}
