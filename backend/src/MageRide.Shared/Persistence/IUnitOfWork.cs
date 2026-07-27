using System.Data;
using Npgsql;

namespace MageRide.Shared.Persistence;

/// <summary>
/// A single <see cref="NpgsqlTransaction"/> shared by the repositories taking part in one
/// multi-statement write (D3' §0 "Multi-statement writes use explicit NpgsqlTransaction").
/// </summary>
/// <remarks>
/// This is the transaction the outbox row is written inside: the domain change and the
/// <c>outbox</c> insert commit together or not at all, so no event can describe a state that
/// was rolled back (R-13).
/// </remarks>
public interface IUnitOfWork : IAsyncDisposable
{
    NpgsqlConnection Connection { get; }

    NpgsqlTransaction Transaction { get; }

    /// <summary><see langword="true"/> once <see cref="CommitAsync"/> or <see cref="RollbackAsync"/> has run.</summary>
    bool IsCompleted { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>Opens units of work. Registered scoped so a request can hold at most one at a time.</summary>
public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default);
}
