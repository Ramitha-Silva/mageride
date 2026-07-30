using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Protocols;
using Microsoft.Extensions.Options;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>Codecs built the way the service builds them, so a test never constructs one by hand.</summary>
/// <remarks>
/// Through the real <see cref="ProtocolCodecFactory"/> and with the real default options — which is
/// what makes the JT/T 808 assertions cover <c>Adapter:Jt808DeviceUtcOffset</c>'s default of UTC+8
/// rather than whatever a test happened to pass.
/// </remarks>
internal static class Codecs
{
    /// <summary>A fresh codec for <paramref name="family"/>, on the deployed defaults.</summary>
    public static IProtocolCodec For(ProtocolFamily family, AdapterOptions? options = null) =>
        new ProtocolCodecFactory(Options.Create(options ?? new AdapterOptions()), TimeProvider.System)
            .Create(family);

    /// <summary>A codec on a fixed clock, for the assertions that depend on "now".</summary>
    public static IProtocolCodec For(ProtocolFamily family, TimeProvider clock, AdapterOptions? options = null) =>
        new ProtocolCodecFactory(Options.Create(options ?? new AdapterOptions()), clock).Create(family);
}
