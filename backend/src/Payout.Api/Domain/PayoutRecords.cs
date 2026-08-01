using System.Security.Cryptography;
using System.Text;

namespace MageRide.Payout.Domain;

/// <summary><c>billing.payouts.status</c> (migration 1109).</summary>
public static class PayoutStatuses
{
    /// <summary>Debited and recorded, not yet handed to a bank — also where a run rests with no adapter.</summary>
    public const string Pending = "PENDING";

    /// <summary>Handed to the bank; waiting for it to say what happened.</summary>
    public const string Submitted = "SUBMITTED";

    /// <summary>Terminal. The money left the platform.</summary>
    public const string Paid = "PAID";

    /// <summary>Terminal. The bank refused it and the debit has already been reversed.</summary>
    public const string Failed = "FAILED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending, Submitted, Paid, Failed,
    };
}

/// <summary><c>billing.payout_batches.status</c> (migration 1109).</summary>
public static class PayoutBatchStatuses
{
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

/// <summary>One weekly sweep.</summary>
public sealed record PayoutBatch(
    Guid Id,
    DateOnly RunDate,
    DateTimeOffset TzAt,
    string Status,
    int InstructionCount,
    long TotalMinor,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>One instruction — a driver's whole balance, on its way to their bank.</summary>
public sealed record PayoutInstruction(
    Guid Id,
    Guid BatchId,
    Guid DriverId,
    Guid PayoutProfileId,
    long AmountMinor,
    string Status,
    string? FailureReason,
    string? ProviderReference,
    Guid JournalEntryId,
    string? AccountNoMasked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A driver the sweep may pay: a verified profile and a balance to move.</summary>
public sealed record EligibleDriver(
    Guid DriverId, Guid PayoutProfileId, long BalanceMinor, string AccountNo);

/// <summary>
/// How a payout id is derived, and why it is not random.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the answer to a fence that cannot be met literally.</b> AL-58 says the wallet debit
/// and the <c>billing.payouts</c> row "commit together" — but they live in two services, so there
/// is no transaction that spans them. What can be made true instead is that a crash between them
/// is <em>recoverable</em>, and that is what a derived id buys.
/// </para>
/// <para>
/// The id is a deterministic function of <c>(batchId, driverId)</c>, and wallet-svc composes its
/// ledger key from the id (<c>driver_payout:{payoutId}</c>). So re-running a batch regenerates the
/// same id, replays the same debit — a no-op, answering <c>replayed: true</c> with the original
/// entry — and completes the insert that did not happen. Nothing is paid twice, and nothing is
/// lost. A random id would make the orphaned debit unfindable and the driver's money with it.
/// </para>
/// <para>
/// <c>ux_payouts_batch_driver</c> is the other half: one instruction per driver per batch, so the
/// completing insert cannot become a second one.
/// </para>
/// <para>
/// RFC 4122 §4.3's name-based scheme with SHA-1, which is what UUID v5 is. Not used as a hash of
/// anything secret — it is a naming function, and its only requirement is that it is stable.
/// </para>
/// </remarks>
public static class PayoutIds
{
    /// <summary>A MageRide-local namespace, so these ids cannot collide with another scheme's.</summary>
    private static readonly Guid Namespace = Guid.Parse("6f8a1d2e-9c47-4b53-9a1f-2d5c7e0b4a86");

    public static Guid For(Guid batchId, Guid driverId)
    {
        var name = Encoding.UTF8.GetBytes($"{batchId:D}:{driverId:D}");

        Span<byte> namespaceBytes = stackalloc byte[16];
        WriteBigEndian(Namespace, namespaceBytes);

        var input = new byte[namespaceBytes.Length + name.Length];
        namespaceBytes.CopyTo(input);
        name.CopyTo(input, namespaceBytes.Length);

        var hash = SHA1.HashData(input);

        Span<byte> id = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(id);

        // Version 5, variant RFC 4122.
        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        return ReadBigEndian(id);
    }

    /// <remarks>
    /// Guid's in-memory layout is little-endian for the first three fields on every platform .NET
    /// runs on, and RFC 4122 hashes the big-endian form — so a naive <c>ToByteArray()</c> would
    /// produce a different id on a different runtime. Written out explicitly for that reason.
    /// </remarks>
    private static void WriteBigEndian(Guid value, Span<byte> destination)
    {
        value.TryWriteBytes(destination);
        (destination[0], destination[3]) = (destination[3], destination[0]);
        (destination[1], destination[2]) = (destination[2], destination[1]);
        (destination[4], destination[5]) = (destination[5], destination[4]);
        (destination[6], destination[7]) = (destination[7], destination[6]);
    }

    private static Guid ReadBigEndian(ReadOnlySpan<byte> source)
    {
        Span<byte> local = stackalloc byte[16];
        source.CopyTo(local);
        (local[0], local[3]) = (local[3], local[0]);
        (local[1], local[2]) = (local[2], local[1]);
        (local[4], local[5]) = (local[5], local[4]);
        (local[6], local[7]) = (local[7], local[6]);

        return new Guid(local);
    }
}
