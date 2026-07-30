using MageRide.TcpAdapter.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.TcpAdapter.Ingest;

/// <summary>
/// ADD §7.7.6's per-pod socket ceiling — "3 pods × 10k sockets each per protocol family".
/// </summary>
/// <remarks>
/// <para>
/// <b>One budget for the pod, shared by every listener.</b> The constraint the number stands for is
/// file descriptors and the 512 MB D7' §2.1 gives Container 9, and both are per process — so four
/// listeners each holding 10 000 sockets would be 40 000 against a ceiling that was sized for one.
/// A deployment that runs one family per pod (the StatefulSet-per-family shape §7.7.1 describes) gets
/// the same number either way.
/// </para>
/// <para>
/// <b>A refused connection is accepted and closed, not left in the backlog.</b> Leaving it queued
/// would make the device wait out its connect timeout against a pod that is full, and its retry would
/// come back to the same place; closing immediately sends a FIN it reacts to now, and the L4 balancer
/// health-checks a listener that keeps refusing.
/// </para>
/// </remarks>
public sealed class SocketBudget
{
    private readonly int _ceiling;
    private int _open;

    public SocketBudget(IOptions<AdapterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _ceiling = options.Value.MaxSockets;
    }

    /// <summary>Sockets currently held.</summary>
    public int Open => Volatile.Read(ref _open);

    /// <summary>The ceiling, so a listener can log it once at start-up.</summary>
    public int Ceiling => _ceiling;

    /// <summary>Takes a slot, or reports that the pod is full.</summary>
    public bool TryAcquire()
    {
        // Increment-then-check rather than a compare-exchange loop: the overshoot is bounded by the
        // number of accept loops (four) and the correction is immediate, whereas a CAS loop on the
        // accept path costs a retry per contended connect during exactly the reconnect storm R-09 is
        // about.
        if (Interlocked.Increment(ref _open) <= _ceiling)
        {
            return true;
        }

        Interlocked.Decrement(ref _open);
        return false;
    }

    /// <summary>Returns a slot.</summary>
    public void Release() => Interlocked.Decrement(ref _open);
}
