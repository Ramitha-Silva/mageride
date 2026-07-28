using MageRide.Iam.Profiles;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Iam.Endpoints;

/// <summary>
/// <c>/v1/me/saved-addresses</c> — Home, Work and the labelled places behind SCR-PA/PI-026
/// (AL-14, AL-26, US-22.1/22.2).
/// </summary>
public static class SavedAddressEndpoints
{
    public static IEndpointRouteBuilder MapSavedAddressEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var addresses = endpoints.MapGroup("/v1/me/saved-addresses")
            .WithTags("saved-addresses")
            .RequireAuthorization();

        addresses.MapGet("/", ListAsync).WithName("listSavedAddresses");
        addresses.MapPost("/", CreateAsync).WithName("createSavedAddress");
        addresses.MapPut("/{addressId}", UpdateAsync).WithName("updateSavedAddress");
        addresses.MapDelete("/{addressId}", DeleteAsync).WithName("deleteSavedAddress");

        return endpoints;
    }

    private static async Task<Ok<SavedAddressListResponse>> ListAsync(
        HttpContext context, ISavedAddressService addresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(addresses);

        var saved = await addresses.ListAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(new SavedAddressListResponse([.. saved.Select(SavedAddressResponse.From)]));
    }

    private static async Task<Created<SavedAddressResponse>> CreateAsync(
        SavedAddressBody? body,
        HttpContext context,
        ISavedAddressService addresses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(addresses);

        var saved = await addresses.CreateAsync(
            context.User.RequireSubjectId(), ToCommand(body), cancellationToken);

        return TypedResults.Created($"/v1/me/saved-addresses/{saved.Id}", SavedAddressResponse.From(saved));
    }

    private static async Task<Ok<SavedAddressResponse>> UpdateAsync(
        string addressId,
        SavedAddressBody? body,
        HttpContext context,
        ISavedAddressService addresses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(addresses);

        var saved = await addresses.UpdateAsync(
            context.User.RequireSubjectId(),
            UserEndpoints.RequireId(addressId, "saved address"),
            ToCommand(body),
            cancellationToken);

        return TypedResults.Ok(SavedAddressResponse.From(saved));
    }

    private static async Task<NoContent> DeleteAsync(
        string addressId, HttpContext context, ISavedAddressService addresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(addresses);

        await addresses.DeleteAsync(
            context.User.RequireSubjectId(), UserEndpoints.RequireId(addressId, "saved address"), cancellationToken);

        return TypedResults.NoContent();
    }

    private static SavedAddressCommand ToCommand(SavedAddressBody? body) => new(
        body?.Label, body?.Line1, body?.Line2, body?.Line3, body?.Lat, body?.Lng, body?.IsHome ?? false, body?.IsWork ?? false);
}
