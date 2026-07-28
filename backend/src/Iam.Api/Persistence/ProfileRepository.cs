using Dapper;
using MageRide.Iam.Domain;
using Npgsql;

namespace MageRide.Iam.Persistence;

/// <summary>
/// The profile columns of <c>iam.users</c> — everything the apps' Profile &amp; Settings screens
/// read and write (D2 SCR-PA/PI-027, 027b; AL-14, AL-26, AL-27).
/// </summary>
public interface IProfileRepository
{
    Task<UserProfile?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the <c>PUT /v1/users/me</c> patch. Every argument is optional and a
    /// <see langword="null"/> leaves the column alone — the contract's body has no required
    /// field, so a client sending only <c>firstName</c> must not blank the photo.
    /// </summary>
    Task<UserProfile> UpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string? firstName,
        string? photoUrl,
        string? language,
        IReadOnlyDictionary<string, bool>? notifPrefs,
        CancellationToken cancellationToken);

    /// <summary>Sets one scalar preference column. <paramref name="column"/> is never caller-supplied.</summary>
    Task<UserProfile> SetPreferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        ProfilePreference preference,
        string value,
        CancellationToken cancellationToken);

    /// <summary>Whether a launch city exists and is currently offered (AL-27).</summary>
    Task<bool> IsOperatingCityActiveAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string code, CancellationToken cancellationToken);
}

/// <summary>The scalar preferences that have their own route. Closed set — see <see cref="IProfileRepository"/>.</summary>
public enum ProfilePreference
{
    Language,
    DefaultPaymentMethod,
    OperatingCityCode,
}

/// <inheritdoc cref="IProfileRepository"/>
public sealed class ProfileRepository : IProfileRepository
{
    private const string Columns =
        "id, phone, email, role, first_name, photo_url, language, operating_city_code, " +
        "default_payment_method, notif_prefs, emergency_contact_name, emergency_contact_phone, " +
        "is_blocked, created_at";

    public async Task<UserProfile?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            $"SELECT {Columns} FROM iam.users WHERE id = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

        return row?.ToProfile();
    }

    public async Task<UserProfile> UpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        string? firstName,
        string? photoUrl,
        string? language,
        IReadOnlyDictionary<string, bool>? notifPrefs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // COALESCE rather than a composed SET list: the patch is sparse and building the SQL from
        // whichever fields arrived would put string concatenation on a write path for no gain.
        // Every parameter is still a parameter.
        var row = await connection.QuerySingleAsync<ProfileRow>(new CommandDefinition(
            $"""
             UPDATE iam.users
                SET first_name  = COALESCE(@FirstName, first_name),
                    photo_url   = COALESCE(@PhotoUrl, photo_url),
                    language    = COALESCE(@Language, language),
                    notif_prefs = COALESCE(@NotifPrefs::jsonb, notif_prefs)
              WHERE id = @UserId
             RETURNING {Columns};
             """,
            new
            {
                UserId = userId,
                FirstName = firstName,
                PhotoUrl = photoUrl,
                Language = language,
                NotifPrefs = notifPrefs is null ? null : NotificationPreferences.Write(notifPrefs),
            },
            transaction,
            cancellationToken: cancellationToken));

        return row.ToProfile();
    }

    public async Task<UserProfile> SetPreferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userId,
        ProfilePreference preference,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The column name is chosen from a closed enum, never interpolated from a request. The
        // value is always a parameter.
        var column = preference switch
        {
            ProfilePreference.Language => "language",
            ProfilePreference.DefaultPaymentMethod => "default_payment_method",
            ProfilePreference.OperatingCityCode => "operating_city_code",
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown preference column."),
        };

        var row = await connection.QuerySingleAsync<ProfileRow>(new CommandDefinition(
            $"UPDATE iam.users SET {column} = @Value WHERE id = @UserId RETURNING {Columns};",
            new { UserId = userId, Value = value },
            transaction,
            cancellationToken: cancellationToken));

        return row.ToProfile();
    }

    public async Task<bool> IsOperatingCityActiveAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM config.operating_cities WHERE code = @Code AND is_active);",
            new { Code = code },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The raw row. <c>notif_prefs</c> arrives as a JSON string because no <c>jsonb</c> handler is
    /// registered for <c>Dictionary&lt;string,bool&gt;</c> globally, and registering one in a
    /// service would mutate Dapper's process-wide state for every other service in the test run.
    /// </summary>
    private sealed record ProfileRow(
        Guid Id,
        string? Phone,
        string? Email,
        string Role,
        string? FirstName,
        string? PhotoUrl,
        string Language,
        string? OperatingCityCode,
        string DefaultPaymentMethod,
        string? NotifPrefs,
        string? EmergencyContactName,
        string? EmergencyContactPhone,
        bool IsBlocked,
        DateTimeOffset CreatedAt)
    {
        public UserProfile ToProfile() => new(
            Id,
            Phone,
            Email,
            Role,
            FirstName,
            PhotoUrl,
            Language,
            OperatingCityCode,
            DefaultPaymentMethod,
            NotificationPreferences.Read(NotifPrefs),
            EmergencyContactName,
            EmergencyContactPhone,
            IsBlocked,
            CreatedAt);
    }
}
