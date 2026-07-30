namespace MageRide.TcpAdapter.Protocols;

/// <summary>
/// The five downlink commands the platform defines (D6' §3.1, ADD §7.7.5,
/// <c>mqtt-topics.md</c> §2.2).
/// </summary>
/// <remarks>
/// <para>
/// The set is closed. An envelope naming anything else is dropped and counted rather than passed
/// through to a device: <c>veh/{vehicleId}/cmd</c> is authorised for the vehicle, not for a shell, and
/// a protocol like GT06 whose command payload is an opaque ASCII string would otherwise turn any
/// publisher on that topic into a device-configuration channel.
/// </para>
/// <para>
/// <b>Not every command exists on every protocol.</b> A codec answers <see langword="null"/> for one
/// it cannot express, and the router counts that as unsupported rather than pretending to have sent
/// it — see <see cref="Publishing.DownlinkRouter"/>. The one command no device frame carries at all is
/// <see cref="RevokeCredential"/>, which the adapter honours by closing the socket: the credential is
/// revoked centrally (T-12) and there is nothing for the device to be told.
/// </para>
/// </remarks>
public static class TrackerCommands
{
    /// <summary>Change the position cadence. <c>args.seconds</c>.</summary>
    public const string SetPosRate = "setPosRate";

    /// <summary>Report a position immediately.</summary>
    public const string PingNow = "pingNow";

    /// <summary>Restart the device.</summary>
    public const string Reboot = "reboot";

    /// <summary>Set a circular geofence. <c>args.lat</c>, <c>args.lng</c>, <c>args.radiusM</c>.</summary>
    public const string SetGeofence = "setGeofence";

    /// <summary>
    /// The credential is no longer valid (T-12). Honoured by closing the socket, not by a frame.
    /// </summary>
    public const string RevokeCredential = "revokeCredential";

    /// <summary>Every command name the platform defines.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { SetPosRate, PingNow, Reboot, SetGeofence, RevokeCredential };

    /// <summary>Whether the name is one of the five.</summary>
    public static bool IsKnown(string? command) => command is not null && All.Contains(command);
}
