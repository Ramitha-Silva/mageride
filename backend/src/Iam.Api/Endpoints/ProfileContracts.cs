using System.Text.Json.Serialization;
using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Iam.Profiles;
using MageRide.Iam.Rbac;
using MageRide.Shared.Auth;
using MageRide.Shared.Primitives;

namespace MageRide.Iam.Endpoints;

// The wire shapes of the profile, saved-address, emergency-contact, bootstrap and RBAC halves of
// backend/contracts/iam.yaml. Same rules as AuthContracts: one record per schema, nullable where
// the contract makes a field optional and nullable where it does not either, so a missing
// required field is a 400 validation-failed rather than a framework 400 with no error code.

/// <summary><c>PUT /v1/users/me</c>.</summary>
public sealed record UpdateProfileBody(
    string? FirstName,
    string? PhotoUrl,
    string? Language,
    [property: JsonConverter(typeof(LiteralKeyDictionaryConverter))]
    IReadOnlyDictionary<string, bool>? NotifPrefs);

/// <summary><c>PUT /v1/me/prefs/language</c> — request and response.</summary>
public sealed record LanguagePreferenceBody(string? Language);

/// <summary><c>PUT /v1/me/prefs/payment-method</c> — request and response.</summary>
public sealed record PaymentMethodPreferenceBody(string? DefaultPaymentMethod);

/// <summary><c>PUT /v1/me/prefs/operating-city</c> — request and response.</summary>
public sealed record OperatingCityPreferenceBody(string? OperatingCityCode);

/// <summary><c>DELETE /v1/users/me</c> — 202.</summary>
public sealed record DeleteAccountResponse(string RequestId);

/// <summary><c>GET /v1/users/lookup</c> — 200.</summary>
public sealed record LookupUserResponse(bool Registered, string? UserId);

/// <summary><c>iam.yaml#/components/schemas/SavedAddressInput</c>.</summary>
public sealed record SavedAddressBody(
    string? Label, string? Line1, string? Line2, string? Line3, double? Lat, double? Lng, bool IsHome, bool IsWork);

/// <summary><c>iam.yaml#/components/schemas/SavedAddress</c>.</summary>
public sealed record SavedAddressResponse(
    string AddressId,
    string Label,
    string Line1,
    string? Line2,
    string? Line3,
    double Lat,
    double Lng,
    bool IsHome,
    bool IsWork)
{
    public static SavedAddressResponse From(SavedAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return new SavedAddressResponse(
            address.Id.ToString(),
            address.Label,
            address.Line1,
            address.Line2,
            address.Line3,
            address.Geo.Latitude,
            address.Geo.Longitude,
            address.IsHome,
            address.IsWork);
    }
}

/// <summary><c>GET /v1/me/saved-addresses</c> — 200.</summary>
public sealed record SavedAddressListResponse(IReadOnlyList<SavedAddressResponse> Items);

/// <summary><c>iam.yaml#/components/schemas/EmergencyContactInput</c>.</summary>
public sealed record EmergencyContactBody(string? Name, string? Phone);

/// <summary><c>iam.yaml#/components/schemas/EmergencyContact</c>.</summary>
public sealed record EmergencyContactResponse(string ContactId, string Name, string Phone, bool IsPrimary)
{
    public static EmergencyContactResponse From(EmergencyContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return new EmergencyContactResponse(contact.Id.ToString(), contact.Name, contact.Phone, contact.IsPrimary);
    }
}

/// <summary><c>GET /v1/me/emergency-contacts</c> — 200.</summary>
public sealed record EmergencyContactListResponse(IReadOnlyList<EmergencyContactResponse> Items);

/// <summary><c>_shared.yaml#/components/schemas/GeoPoint</c>.</summary>
public sealed record GeoPointResponse(double Lat, double Lng)
{
    public static GeoPointResponse? From(GeoPoint? point) =>
        point is { } value ? new GeoPointResponse(value.Latitude, value.Longitude) : null;
}

/// <summary><c>_shared.yaml#/components/schemas/Money</c>.</summary>
public sealed record MoneyResponse(long AmountMinor, string Currency)
{
    public static MoneyResponse From(Money money) => new(money.AmountMinor, money.Currency);
}

/// <summary><c>iam.yaml#/components/schemas/OperatingCity</c>.</summary>
public sealed record OperatingCityResponse(
    string Code, string NameEn, string NameSi, string NameTa, GeoPointResponse Centroid, int SortOrder)
{
    public static OperatingCityResponse From(OperatingCity city)
    {
        ArgumentNullException.ThrowIfNull(city);

        return new OperatingCityResponse(
            city.Code,
            city.NameEn,
            city.NameSi,
            city.NameTa,
            new GeoPointResponse(city.CentroidLat, city.CentroidLng),
            city.SortOrder);
    }
}

/// <summary><c>iam.yaml#/components/schemas/ActiveTrip</c>.</summary>
public sealed record ActiveTripResponse(
    string TripId,
    string Kind,
    string Role,
    string State,
    string? Mode,
    string? VehicleId,
    string? CounterpartyId,
    GeoPointResponse? Pickup,
    GeoPointResponse? Dropoff,
    DateTimeOffset StartedAt)
{
    public static ActiveTripResponse? From(ActiveTrip? trip) =>
        trip is null
            ? null
            : new ActiveTripResponse(
                trip.TripId.ToString(),
                trip.Kind,
                trip.Role,
                trip.State,
                trip.Mode,
                trip.VehicleId?.ToString(),
                trip.CounterpartyId?.ToString(),
                GeoPointResponse.From(trip.Pickup),
                GeoPointResponse.From(trip.Dropoff),
                trip.StartedAt);
}

/// <summary><c>iam.yaml#/components/schemas/DriverShift</c>.</summary>
public sealed record DriverShiftResponse(
    bool IsOnline,
    string? ActiveSessionId,
    string? ActiveVehicleId,
    DateOnly BusinessDate,
    int TodayTrips,
    MoneyResponse TodayGross,
    MoneyResponse TodayDailyFee)
{
    public static DriverShiftResponse? From(DriverShift? shift) =>
        shift is null
            ? null
            : new DriverShiftResponse(
                shift.IsOnline,
                shift.ActiveSessionId?.ToString(),
                shift.ActiveVehicleId?.ToString(),
                shift.BusinessDate,
                shift.TodayTrips,
                MoneyResponse.From(shift.TodayGross),
                MoneyResponse.From(shift.TodayDailyFee));
}

/// <summary><c>iam.yaml#/components/schemas/AppConfig</c>.</summary>
public sealed record AppConfigResponse(
    IReadOnlyList<OperatingCityResponse> Cities,
    [property: JsonConverter(typeof(LiteralKeyDictionaryConverter))]
    IReadOnlyDictionary<string, bool> FeatureFlags);

/// <summary><c>iam.yaml#/components/schemas/LoginBootstrap</c> — the AL-14 eager-fetch set.</summary>
public sealed record LoginBootstrapResponse(
    UserProfileResponse Profile,
    IReadOnlyList<SavedAddressResponse> SavedAddresses,
    IReadOnlyList<EmergencyContactResponse> EmergencyContacts,
    string DefaultPaymentMethod,
    IReadOnlyList<string> PaymentMethods,
    ActiveTripResponse? ActiveTrip,
    DriverShiftResponse? Driver,
    AppConfigResponse Config,
    EffectivePermissionsResponse Permissions)
{
    public static LoginBootstrapResponse From(LoginBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);

        return new LoginBootstrapResponse(
            UserProfileResponse.From(bootstrap.Profile, bootstrap.Roles, bootstrap.Fleet),
            [.. bootstrap.SavedAddresses.Select(SavedAddressResponse.From)],
            [.. bootstrap.EmergencyContacts.Select(EmergencyContactResponse.From)],
            bootstrap.Profile.DefaultPaymentMethod,
            bootstrap.PaymentMethods,
            ActiveTripResponse.From(bootstrap.ActiveTrip),
            DriverShiftResponse.From(bootstrap.Driver),
            new AppConfigResponse([.. bootstrap.Cities.Select(OperatingCityResponse.From)], bootstrap.FeatureFlags),
            EffectivePermissionsResponse.From(bootstrap.Permissions));
    }
}

/// <summary><c>iam.yaml#/components/schemas/PermissionEntry</c>.</summary>
public sealed record PermissionEntryResponse(
    string FeatureArea,
    string Label,
    IReadOnlyList<string> Grants,
    IReadOnlyList<string> ScopedGrants,
    string Symbol,
    string? Qualifier)
{
    public static PermissionEntryResponse From(EffectivePermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);

        return new PermissionEntryResponse(
            permission.Area.Key,
            permission.Area.Label,
            Names(permission.Grants),
            Names(permission.ScopedGrants),
            permission.Symbol,
            permission.Qualifier);
    }

    public static PermissionEntryResponse From(FeatureArea area, PermissionCell cell)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(cell);

        // A single matrix cell is uniform: if it is scoped at all, every capability in it is.
        var scoped = cell.Grants.HasFlag(PermissionGrant.OwnScope) ? cell.Grants : PermissionGrant.None;

        return new PermissionEntryResponse(
            area.Key, area.Label, Names(cell.Grants), Names(scoped), cell.Symbol, cell.Qualifier);
    }

    /// <summary>
    /// The flags as the contract's <c>PermissionGrant</c> enum spells them.
    /// </summary>
    /// <remarks>
    /// Projected by hand rather than left to <c>JsonStringEnumConverter</c>: a <c>[Flags]</c> enum
    /// serialises as one comma-joined string, and the contract declares an array. Ordered as the
    /// enum declares them so two identical grant sets always render identically — a client may
    /// compare them.
    /// </remarks>
    private static IReadOnlyList<string> Names(PermissionGrant grants)
    {
        var names = new List<string>(5);

        if (grants.HasFlag(PermissionGrant.Read))
        {
            names.Add("read");
        }

        if (grants.HasFlag(PermissionGrant.Write))
        {
            names.Add("write");
        }

        if (grants.HasFlag(PermissionGrant.Configure))
        {
            names.Add("configure");
        }

        if (grants.HasFlag(PermissionGrant.Raise))
        {
            names.Add("raise");
        }

        if (grants.HasFlag(PermissionGrant.OwnScope))
        {
            names.Add("ownScope");
        }

        return names;
    }
}

/// <summary><c>iam.yaml#/components/schemas/EffectivePermissions</c>.</summary>
public sealed record EffectivePermissionsResponse(
    string UserId,
    IReadOnlyList<string> Roles,
    string? FleetRole,
    string? FleetId,
    IReadOnlyList<PermissionEntryResponse> Permissions)
{
    public static EffectivePermissionsResponse From(EffectivePermissionSet effective)
    {
        ArgumentNullException.ThrowIfNull(effective);

        return new EffectivePermissionsResponse(
            effective.UserId.ToString(),
            effective.Roles,
            effective.Fleet?.FleetRole,
            effective.Fleet?.FleetId.ToString(),
            [.. effective.Permissions.Select(PermissionEntryResponse.From)]);
    }
}

/// <summary>One row of <c>iam.yaml#/components/schemas/PermissionMatrix</c>.</summary>
public sealed record PermissionMatrixRowResponse(
    string FeatureArea, string Label, IReadOnlyDictionary<string, PermissionEntryResponse> Cells);

/// <summary><c>GET /v1/admin/rbac/matrix</c> — 200.</summary>
public sealed record PermissionMatrixResponse(
    IReadOnlyList<string> Roles, IReadOnlyList<PermissionMatrixRowResponse> Areas)
{
    public static PermissionMatrixResponse Build() => new(
        PermissionMatrix.Columns,
        [
            .. FeatureAreas.All.Select(area => new PermissionMatrixRowResponse(
                area.Key,
                area.Label,
                PermissionMatrix.Row(area).ToDictionary(
                    static cell => cell.Key,
                    cell => PermissionEntryResponse.From(area, cell.Value),
                    StringComparer.Ordinal))),
        ]);
}

/// <summary><c>iam.yaml#/components/schemas/RoleCatalogEntry</c>.</summary>
public sealed record RoleCatalogEntryResponse(string Role, string Label, bool IsInternal)
{
    public static RoleCatalogEntryResponse From(RoleCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new RoleCatalogEntryResponse(entry.Role, entry.Label, entry.IsInternal);
    }
}

/// <summary><c>GET /v1/admin/rbac/roles</c> — 200.</summary>
public sealed record RoleCatalogResponse(IReadOnlyList<RoleCatalogEntryResponse> Items);

/// <summary><c>POST /v1/admin/rbac/users/{userId}/roles</c>.</summary>
public sealed record GrantRoleBody(string? Role);

/// <summary><c>iam.yaml#/components/schemas/UserRoleGrants</c>.</summary>
public sealed record UserRoleGrantsResponse(
    string UserId,
    string PrimaryRole,
    IReadOnlyList<string> Roles,
    string? FleetRole,
    string? FleetId,
    EffectivePermissionsResponse Permissions)
{
    public static UserRoleGrantsResponse From(UserRoleGrants grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        return new UserRoleGrantsResponse(
            grants.UserId.ToString(),
            grants.PrimaryRole,
            grants.Roles,
            grants.Fleet?.FleetRole,
            grants.Fleet?.FleetId.ToString(),
            EffectivePermissionsResponse.From(grants.Permissions));
    }
}
