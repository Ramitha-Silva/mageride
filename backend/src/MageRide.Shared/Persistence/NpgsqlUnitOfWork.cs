using System.Data;
using Npgsql;

namespace MageRide.Shared.Persistence;

/// <inheritdoc cref="IUnitOfWork"/>
public sealed class NpgsqlUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _disposed;

    private NpgsqlUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public NpgsqlConnection Connection =>
        _disposed ? throw new ObjectDisposedException(nameof(NpgsqlUnitOfWork)) : _connection;

    public NpgsqlTransaction Transaction =>
        _disposed ? throw new ObjectDisposedException(nameof(NpgsqlUnitOfWork)) : _transaction;

    public bool IsCompleted { get; private set; }

    internal static async Task<NpgsqlUnitOfWork> BeginAsync(
        INpgsqlConnectionFactory factory, IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        var connection = await factory.OpenAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return new NpgsqlUnitOfWork(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await _transaction.CommitAsync(cancellationToken);
        IsCompleted = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await _transaction.RollbackAsync(cancellationToken);
        IsCompleted = true;
    }

    /// <summary>Rolls back if neither commit nor rollback ran — an escaping exception must not commit.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // NpgsqlTransaction.DisposeAsync already rolls back an uncommitted transaction.
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private void ThrowIfCompleted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsCompleted)
        {
            throw new InvalidOperationException("This unit of work has already been committed or rolled back.");
        }
    }
}

/// <inheritdoc cref="IUnitOfWorkFactory"/>
public sealed class NpgsqlUnitOfWorkFactory(INpgsqlConnectionFactory connectionFactory) : IUnitOfWorkFactory
{
    public async Task<IUnitOfWork> BeginAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default) =>
        await NpgsqlUnitOfWork.BeginAsync(connectionFactory, isolationLevel, cancellationToken);
}
