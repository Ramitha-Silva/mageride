using MageRide.Shared.Auth;
using Microsoft.AspNetCore.SignalR;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// Makes SignalR's per-user addressing use the access token's <c>sub</c> (D-29).
/// </summary>
/// <remarks>
/// SignalR's default provider reads <c>ClaimTypes.NameIdentifier</c>, which a MageRide token does not
/// carry: the shared kernel sets <c>MapInboundClaims = false</c> so claims arrive under their JWT
/// names, and <c>NameClaimType</c> is <c>sub</c>. Without this, <c>Clients.User(...)</c> addresses
/// nobody — silently, because a send to an unknown user is not an error — and the D-22 revocation
/// would appear to work while reaching no one.
/// </remarks>
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection?.User?.FindFirst(MageRideClaims.Subject)?.Value;
}
