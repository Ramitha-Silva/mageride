using System.Net;
using System.Net.Sockets;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>
/// A hardware tracker, as far as the adapter is concerned: a TCP socket that writes frames and reads
/// whatever comes back.
/// </summary>
/// <remarks>
/// A real socket rather than a stubbed stream, because the claims are about the socket: "an unbound
/// IMEI is refused at connect" is the adapter closing this one, and "a half-closed socket publishes a
/// retained status=offline" is <see cref="HalfCloseAsync"/> — a FIN with the read half still open,
/// which is what a device losing its uplink actually does and what
/// <c>NetworkStream.ReadAsync</c> returning zero on the far side means.
/// </remarks>
internal sealed class DeviceSocket : IAsyncDisposable
{
    private readonly Socket _socket;

    private DeviceSocket(Socket socket) => _socket = socket;

    /// <summary>Connects to a listener on the loopback.</summary>
    public static async Task<DeviceSocket> ConnectAsync(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));

        return new DeviceSocket(socket);
    }

    /// <summary>Whether the socket still looks connected from this side.</summary>
    public bool Connected => _socket.Connected;

    /// <summary>Writes a frame.</summary>
    public async Task SendAsync(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        await _socket.SendAsync(frame, SocketFlags.None);
    }

    /// <summary>
    /// Reads whatever the adapter writes back, or an empty array when it closes instead.
    /// </summary>
    /// <remarks>
    /// A timeout is not a failure here: three of the four protocols leave a position frame
    /// unacknowledged, and the tests that assert a refusal are waiting for a close rather than a reply.
    /// </remarks>
    public async Task<byte[]> ReceiveAsync(TimeSpan? timeout = null)
    {
        var buffer = new byte[512];

        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));

        try
        {
            var read = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellation.Token);

            return buffer.AsSpan(0, read).ToArray();
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException)
        {
            return [];
        }
    }

    /// <summary>
    /// Waits for the adapter to close this socket, and reports whether it did.
    /// </summary>
    /// <remarks>
    /// A refused device is observed as a zero-length read, which is the FIN the adapter sends — not as
    /// an exception, and not by asking <see cref="Socket.Connected"/>, which reports the state of the
    /// last operation rather than the state of the connection.
    /// </remarks>
    public async Task<bool> WaitForCloseAsync(TimeSpan? timeout = null)
    {
        var buffer = new byte[256];
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            try
            {
                if (await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellation.Token) == 0)
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                // Nothing yet; the adapter may still be resolving the device.
            }
            catch (SocketException)
            {
                // A reset rather than a graceful close. Still closed.
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shuts down the write half only — the half-close T-04 is written against.
    /// </summary>
    /// <remarks>
    /// Deliberately not a full close: the adapter's read loop must see <c>ReadAsync</c> return zero and
    /// treat it as the device going away, and this suite has to be able to tell that apart from a socket
    /// this side tore down entirely.
    /// </remarks>
    public void HalfCloseAsync() => _socket.Shutdown(SocketShutdown.Send);

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // Already gone — which several of these tests arrange on purpose.
        }

        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
