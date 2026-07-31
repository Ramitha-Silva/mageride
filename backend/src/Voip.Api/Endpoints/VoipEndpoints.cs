using System.Security.Claims;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Voip.Domain;
using MageRide.Voip.Signalling;

namespace MageRide.Voip.Endpoints;

/// <summary>`POST /v1/voip/token` — mint a signalling token for a ride.</summary>
public sealed record VoipTokenRequest(Guid RideId);

/// <summary>D3' voip-svc's 200 shape.</summary>
public sealed record VoipTokenResponse(string RoomName, string Token, string WsUrl, string Callee);

/// <summary>`POST /v1/calls/start`.</summary>
public sealed record StartCallRequest(Guid RideId, string CalleeRole, string CallType);

/// <summary>The LiveKit session, present only for <c>free_voip</c>.</summary>
public sealed record CallSession(string RoomName, string Token, string WsUrl);

public sealed record StartCallResponse(Guid CallId, string CallType, CallSession? Session);

/// <summary>`POST /v1/calls/{callId}/outcome` — Δ C055.</summary>
public sealed record CallOutcomeRequest(string Outcome);

/// <summary>
/// voip-svc's whole surface: two routes from D3' and one Δ C055.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no route here that returns a phone number, and there is no code that could.</b>
/// AL-48 puts the counterparty's MSISDN on ride-svc's <c>GET /v1/rides/{id}</c> post-accept; "Normal
/// call" is a client-side <c>tel:</c> dial with no server round-trip. The masked bridge, the
/// proxy-DID lease and the D-25 SMS relay are removed and must not come back.
/// </para>
/// <para>
/// <b>Attestation is the gateway's</b> (C008, D-30). <c>voip.yaml</c> declares
/// <c>X-Attestation</c> on the token route because the edge enforces it before forwarding; this
/// service checks the bearer and the ride, which is what it can actually decide.
/// </para>
/// </remarks>
public static class VoipEndpoints
{
    public static IEndpointRouteBuilder MapVoipEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var voip = routes.MapGroup("/v1/voip").RequireAuthorization().WithTags("voip");

        voip.MapPost("/token", IssueTokenAsync)
            .WithName("issueVoipToken")
            .WithSummary("Mints a LiveKit signalling token for a ride (D-24, P-05).");

        var calls = routes.MapGroup("/v1/calls").RequireAuthorization().WithTags("voip");

        calls.MapPost("/start", StartCallAsync)
            .WithName("startCall")
            .WithSummary("Starts an in-app call, or records a direct-dial tap (AL-48).");

        calls.MapPost("/{callId:guid}/outcome", RecordOutcomeAsync)
            .WithName("recordCallOutcome")
            .WithSummary("Records how a call ended — including the VoIP failure the fallback hangs on.");

        return routes;
    }

    private static async Task<IResult> IssueTokenAsync(
        VoipTokenRequest body, ClaimsPrincipal caller, ICallService calls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.RideId == Guid.Empty)
        {
            throw new MageRideException(MageRideErrors.ValidationFailed, "rideId is required.");
        }

        var issued = await calls.IssueTokenAsync(body.RideId, UserId(caller), cancellationToken);

        return Results.Ok(new VoipTokenResponse(
            issued.Token.RoomName,
            issued.Token.Token,
            issued.Token.WsUrl,
            issued.Callee == CallParty.Driver ? "driver" : "rider"));
    }

    private static async Task<IResult> StartCallAsync(
        StartCallRequest body, ClaimsPrincipal caller, ICallService calls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.RideId == Guid.Empty)
        {
            throw new MageRideException(MageRideErrors.ValidationFailed, "rideId is required.");
        }

        var started = await calls.StartCallAsync(
            body.RideId, UserId(caller), body.CalleeRole, body.CallType, cancellationToken);

        return Results.Ok(new StartCallResponse(
            started.CallId,
            started.CallType,
            started.Session is { } session
                ? new CallSession(session.RoomName, session.Token, session.WsUrl)
                : null));
    }

    private static async Task<IResult> RecordOutcomeAsync(
        Guid callId,
        CallOutcomeRequest body,
        ClaimsPrincipal caller,
        ICallService calls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        await calls.RecordOutcomeAsync(callId, UserId(caller), body.Outcome, cancellationToken);

        return Results.NoContent();
    }

    /// <summary>The caller, from the token's <c>sub</c>. Never from the body.</summary>
    private static Guid UserId(ClaimsPrincipal caller) => caller.RequireSubjectId();
}
