using MageRide.Registry.Domain;
using MageRide.Registry.Sharing;
using MageRide.Registry.Vehicles;

namespace MageRide.Registry.Endpoints;

// The wire shapes of the C028 half of backend/contracts/registry.yaml. Nullable where the
// contract makes a field optional, and nullable where it does not either — a missing required
// field must come back as 400 validation-failed, not as a framework 400 with no error code.

/// <summary><c>registry.yaml#/components/schemas/VehicleDetail</c>.</summary>
/// <param name="Source">
/// <c>owned</c> or <c>assigned</c>. Additive to the contract, which has nowhere to say how the
/// caller came by the vehicle — US-13.9 needs it to render the "Temporarily assigned to me"
/// group. See the C028 handoff.
/// </param>
public sealed record VehicleDetailResponse(
    string VehicleId,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string Status,
    string OnboardingStatus,
    string DispatchState,
    string DriverName,
    string? DriverPhotoUrl,
    string Source,
    string? FleetId,
    bool IsSelected,
    bool IsGoLiveEligible,
    DateTimeOffset CreatedAt)
{
    public static VehicleDetailResponse From(DriverVehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        var entitlement = vehicle.Entitlement;

        return new VehicleDetailResponse(
            entitlement.VehicleId.ToString(),
            entitlement.RegistrationNumber,
            entitlement.VehicleType,
            entitlement.Mode,
            entitlement.Status,
            entitlement.OnboardingStatus,
            entitlement.DispatchState,
            entitlement.DriverName,
            entitlement.DriverPhotoUrl,
            entitlement.Source,
            entitlement.FleetId?.ToString(),
            vehicle.IsSelected,
            entitlement.IsGoLiveEligible,
            entitlement.CreatedAt);
    }
}

/// <summary>200 body of <c>GET /v1/vehicles/{vehicleId}/status</c> (US-2.13/2.15).</summary>
public sealed record VehicleStatusResponse(string Status, string? RejectionReason);

/// <summary>Body of <c>PUT /v1/vehicles/{vehicleId}/driver-profile</c> (US-2.12).</summary>
public sealed record UpdateDriverProfileBody(string? Name, string? PhotoUrl);

/// <summary>Body of <c>POST /v1/vehicles/{vehicleId}/share</c> (US-4.1/4.2).</summary>
public sealed record CreateShareBody(string? UserId, DateTimeOffset? ExpiresAt);

/// <summary>201 body of <c>POST /v1/vehicles/{vehicleId}/share</c>.</summary>
public sealed record CreateShareResponse(string GrantId)
{
    public static CreateShareResponse From(ShareGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new CreateShareResponse(grant.Id.ToString());
    }
}

/// <summary>200 body of <c>POST /v1/vehicles/{vehicleId}/share/{grantId}/accept</c>.</summary>
public sealed record AcceptShareResponse(string GrantId, string Status)
{
    /// <summary>The contract's enum has one value, <c>active</c> — an accepted grant is a live one.</summary>
    public static AcceptShareResponse From(ShareGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new AcceptShareResponse(grant.Id.ToString(), "active");
    }
}

/// <summary><c>registry.yaml#/components/schemas/Subscriber</c>.</summary>
public sealed record SubscriberResponse(
    string UserId, string GrantId, string Status, DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt)
{
    public static SubscriberResponse From(Subscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);

        return new SubscriberResponse(
            subscriber.PassengerId.ToString(),
            subscriber.GrantId.ToString(),
            subscriber.Status,
            subscriber.GrantedAt,
            subscriber.ExpiresAt);
    }
}

/// <summary>200 body of <c>GET /v1/vehicles/{vehicleId}/subscribers</c> — a <c>CursorPage</c>.</summary>
public sealed record SubscriberPageResponse(IReadOnlyList<SubscriberResponse> Items, string? NextCursor)
{
    public static SubscriberPageResponse From(SubscriberPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new SubscriberPageResponse([.. page.Items.Select(SubscriberResponse.From)], page.NextCursor);
    }
}

/// <summary>Body of <c>POST /v1/share-requests</c> (US-4.5).</summary>
public sealed record ShareRequestBody(string? VehicleId);

/// <summary>201 body of <c>POST /v1/share-requests</c>.</summary>
public sealed record ShareRequestResponse(string RequestId, string Status)
{
    public static ShareRequestResponse From(AccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ShareRequestResponse(request.Id.ToString(), request.Status);
    }
}

/// <summary>Body of <c>POST /v1/internal/vehicles/{vehicleId}/merchant</c> (D-11).</summary>
public sealed record BindMerchantBody(string? MerchantId, string? MerchantRef);

/// <summary>200 body of the merchant bind.</summary>
public sealed record BindMerchantResponse(string VehicleId, string MerchantId);
