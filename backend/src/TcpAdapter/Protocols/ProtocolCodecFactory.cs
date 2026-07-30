using MageRide.TcpAdapter.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.TcpAdapter.Protocols;

/// <summary>Builds a decoder for one session.</summary>
/// <remarks>
/// <b>Per session, not per process.</b> A JT/T 808 codec remembers which header shape the device used
/// and what terminal number to address a reply to — state that belongs to one socket and would be
/// wrong for the next one. GT06, H02 and NMEA are stateless and get their own instance anyway, because
/// a factory that sometimes shares and sometimes does not is the kind of thing that works until a
/// protocol grows a field.
/// </remarks>
public interface IProtocolCodecFactory
{
    /// <summary>A fresh decoder for <paramref name="family"/>.</summary>
    IProtocolCodec Create(ProtocolFamily family);
}

/// <inheritdoc cref="IProtocolCodecFactory"/>
public sealed class ProtocolCodecFactory(IOptions<AdapterOptions> options, TimeProvider clock) : IProtocolCodecFactory
{
    private readonly AdapterOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IProtocolCodec Create(ProtocolFamily family) => family switch
    {
        ProtocolFamily.Gt06 => new Gt06Codec(_clock),
        ProtocolFamily.Jt808 => new Jt808Codec(_options.Jt808DeviceUtcOffset),
        ProtocolFamily.H02 => new H02Codec(_clock),
        ProtocolFamily.NmeaUdp => new NmeaCodec(_clock),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
