using MageRide.Iam.Profiles;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Iam.Endpoints;

/// <summary>
/// <c>/v1/users</c> and <c>/v1/me</c> — the identity data plane: profile, preferences, saved
/// addresses, emergency contacts, the eager-fetch payload and the PDPA erasure request.
/// </summary>
/// <remarks>
/// <para>
/// Every route here is <b>self-scoped</b>. The subject id comes from the token and never from the
/// path or the body, so there is no route on which one user can name another — which is why none
/// of them carries a URD §2.3 feature gate. The privileged surface is
/// <see cref="RbacEndpoints"/>, and <c>/v1/users/lookup</c> is service-to-service.
/// </para>
/// <para>
/// <b>Language is on two routes and that is deliberate.</b> AL-26 removed the language picker
/// from Edit-profile and kept it in onboarding and Settings, which is a rule about
/// <em>screens</em> — D2 SCR-PA/PI-027b still draws a segmented control there and is the earlier
/// document (see the C027 handoff). The server cannot tell which screen a <c>PUT</c> came from,
/// so <c>iam.yaml</c>'s answer stands: <c>PUT /v1/users/me</c> accepts <c>language</c> because
/// the contract lists it, and <c>PUT /v1/me/prefs/language</c> is the route onboarding and
/// Settings use. The fence is enforced in the apps (C068+), not here, and pretending otherwise
/// by 400-ing a field the contract declares would only break a compliant client.
/// </para>
/// </remarks>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var users = endpoints.MapGroup("/v1/users").WithTags("users").RequireAuthorization();

        users.MapGet("/me", GetProfileAsync).WithName("getMyProfile");
        users.MapPut("/me", UpdateProfileAsync).WithName("updateMyProfile");
        users.MapDelete("/me", DeleteAccountAsync).WithName("deleteMyAccount");

        var me = endpoints.MapGroup("/v1/me").WithTags("users").RequireAuthorization();

        me.MapGet("/bootstrap", BootstrapAsync).WithName("getLoginBootstrap");
        me.MapPut("/prefs/language", SetLanguageAsync).WithName("setLanguagePreference");
        me.MapPut("/prefs/payment-method", SetPaymentMethodAsync).WithName("setDefaultPaymentMethod");
        me.MapPut("/prefs/operating-city", SetOperatingCityAsync).WithName("setOperatingCity");

        return endpoints;
    }

    private static async Task<Ok<UserProfileResponse>> GetProfileAsync(
        HttpContext context, IProfileService profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var view = await profiles.GetAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(UserProfileResponse.From(view.Profile, view.Roles, view.Fleet));
    }

    private static async Task<Ok<UserProfileResponse>> UpdateProfileAsync(
        UpdateProfileBody? body, HttpContext context, IProfileService profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var view = await profiles.UpdateAsync(
            context.User.RequireSubjectId(),
            new UpdateProfileCommand(body?.FirstName, body?.PhotoUrl, body?.Language, body?.NotifPrefs),
            cancellationToken);

        return TypedResults.Ok(UserProfileResponse.From(view.Profile, view.Roles, view.Fleet));
    }

    /// <summary><c>DELETE /v1/users/me</c> — 202 and a <c>pdpa.requests</c> row (US-1.8, E-06).</summary>
    private static async Task<Accepted<DeleteAccountResponse>> DeleteAccountAsync(
        HttpContext context, IProfileService profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var request = await profiles.RequestErasureAsync(context.User.RequireSubjectId(), cancellationToken);

        // The location is admin-bff's GET /v1/pdpa/{requestId} (C065), which is where the client
        // polls. Naming it here rather than in the client keeps the poll target with the thing
        // that created it.
        return TypedResults.Accepted(
            $"/v1/pdpa/{request.Id}", new DeleteAccountResponse(request.Id.ToString()));
    }

    private static async Task<Ok<LoginBootstrapResponse>> BootstrapAsync(
        HttpContext context, IBootstrapService bootstrap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bootstrap);

        var payload = await bootstrap.BuildAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(LoginBootstrapResponse.From(payload));
    }

    private static async Task<Ok<LanguagePreferenceBody>> SetLanguageAsync(
        LanguagePreferenceBody? body, HttpContext context, IProfileService profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var updated = await profiles.SetLanguageAsync(
            context.User.RequireSubjectId(), body?.Language, cancellationToken);

        return TypedResults.Ok(new LanguagePreferenceBody(updated.Language));
    }

    private static async Task<Ok<PaymentMethodPreferenceBody>> SetPaymentMethodAsync(
        PaymentMethodPreferenceBody? body,
        HttpContext context,
        IProfileService profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var updated = await profiles.SetDefaultPaymentMethodAsync(
            context.User.RequireSubjectId(), body?.DefaultPaymentMethod, cancellationToken);

        return TypedResults.Ok(new PaymentMethodPreferenceBody(updated.DefaultPaymentMethod));
    }

    private static async Task<Ok<OperatingCityPreferenceBody>> SetOperatingCityAsync(
        OperatingCityPreferenceBody? body,
        HttpContext context,
        IProfileService profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var updated = await profiles.SetOperatingCityAsync(
            context.User.RequireSubjectId(), body?.OperatingCityCode, cancellationToken);

        return TypedResults.Ok(new OperatingCityPreferenceBody(updated.OperatingCityCode));
    }

    /// <summary>Parses a path id, or 404 — a malformed id names nothing that exists.</summary>
    internal static Guid RequireId(string? value, string what)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new MageRideException(MageRideErrors.NotFound, $"No {what} '{value}'.");
        }

        return parsed;
    }
}
