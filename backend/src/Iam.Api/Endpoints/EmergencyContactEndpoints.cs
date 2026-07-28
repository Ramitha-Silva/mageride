using MageRide.Iam.Profiles;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Iam.Endpoints;

/// <summary>
/// <c>/v1/me/emergency-contacts</c> — the SOS contacts safety-svc fans out to (AL-13, US-12.1).
/// </summary>
/// <remarks>
/// Not driver-only. AL-13 and D2 SCR-PA/PI-027b put the list on the *driver* profile, but the
/// same screen id exists for the passenger app and <c>POST /v1/sos</c> is a passenger action too
/// (US-12.9). Gating this on the driver role would leave a passenger's SOS with nobody to call
/// for the sake of a restriction no spec asks for.
/// </remarks>
public static class EmergencyContactEndpoints
{
    public static IEndpointRouteBuilder MapEmergencyContactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var contacts = endpoints.MapGroup("/v1/me/emergency-contacts")
            .WithTags("emergency-contacts")
            .RequireAuthorization();

        contacts.MapGet("/", ListAsync).WithName("listEmergencyContacts");
        contacts.MapPost("/", CreateAsync).WithName("createEmergencyContact");
        contacts.MapPut("/{contactId}", UpdateAsync).WithName("updateEmergencyContact");
        contacts.MapDelete("/{contactId}", DeleteAsync).WithName("deleteEmergencyContact");

        return endpoints;
    }

    private static async Task<Ok<EmergencyContactListResponse>> ListAsync(
        HttpContext context, IEmergencyContactService contacts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contacts);

        var saved = await contacts.ListAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(new EmergencyContactListResponse([.. saved.Select(EmergencyContactResponse.From)]));
    }

    private static async Task<Created<EmergencyContactResponse>> CreateAsync(
        EmergencyContactBody? body,
        HttpContext context,
        IEmergencyContactService contacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contacts);

        var created = await contacts.CreateAsync(
            context.User.RequireSubjectId(), new EmergencyContactCommand(body?.Name, body?.Phone), cancellationToken);

        return TypedResults.Created(
            $"/v1/me/emergency-contacts/{created.Id}", EmergencyContactResponse.From(created));
    }

    private static async Task<Ok<EmergencyContactResponse>> UpdateAsync(
        string contactId,
        EmergencyContactBody? body,
        HttpContext context,
        IEmergencyContactService contacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contacts);

        var updated = await contacts.UpdateAsync(
            context.User.RequireSubjectId(),
            UserEndpoints.RequireId(contactId, "emergency contact"),
            new EmergencyContactCommand(body?.Name, body?.Phone),
            cancellationToken);

        return TypedResults.Ok(EmergencyContactResponse.From(updated));
    }

    private static async Task<NoContent> DeleteAsync(
        string contactId,
        HttpContext context,
        IEmergencyContactService contacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contacts);

        await contacts.DeleteAsync(
            context.User.RequireSubjectId(), UserEndpoints.RequireId(contactId, "emergency contact"), cancellationToken);

        return TypedResults.NoContent();
    }
}
