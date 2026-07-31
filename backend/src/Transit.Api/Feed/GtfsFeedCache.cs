using MageRide.Transit.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Transit.Feed;

/// <summary>The feed every request is answered from.</summary>
public interface IGtfsFeedCache
{
    /// <summary>The currently published feed. Never null — <see cref="GtfsFeed.Empty"/> before the first load.</summary>
    GtfsFeed Current { get; }

    /// <summary>Reloads if the active feed differs from the one held. Returns whether it swapped.</summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>One reference, swapped.</b> A reload builds a whole new <see cref="GtfsFeed"/> and publishes
/// it with a single assignment, matching AL-54's own shape on the database side: activation swaps
/// the live tables in one transaction, and this swaps the loaded copy in one write. A request
/// holding the old feed finishes on the old feed rather than seeing a half-built one.
/// </para>
/// <para>
/// <b>Reloads are serialised and de-duplicated.</b> A <c>NOTIFY</c> and the safety-net poll can
/// arrive together, and loading a national feed twice concurrently would double the memory for the
/// duration. The gate makes the second caller wait and then find nothing to do.
/// </para>
/// </remarks>
internal sealed class GtfsFeedCache : IGtfsFeedCache
{
    private readonly IGtfsFeedRepository _repository;
    private readonly ILogger<GtfsFeedCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile GtfsFeed _current = GtfsFeed.Empty;

    public GtfsFeedCache(IGtfsFeedRepository repository, ILogger<GtfsFeedCache> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public GtfsFeed Current => _current;

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var active = await _repository.FindActiveAsync(cancellationToken);

            if (active is null)
            {
                if (_current.IsActive)
                {
                    // Every feed archived and none activated. Not a fault — AL-55's safety net —
                    // but it changes every answer this service gives, so it is not a debug line.
                    _logger.LogWarning(
                        "No GTFS feed is active; route matching is now degraded for every corridor (AL-55).");

                    _current = GtfsFeed.Empty;

                    return true;
                }

                return false;
            }

            if (_current.FeedVersionId == active.FeedVersionId)
            {
                return false;
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var feed = await _repository.LoadAsync(active, cancellationToken);

            _current = feed;

            _logger.LogInformation(
                "Loaded GTFS feed {FeedVersionId} ({FeedInfoVersion}) in {Elapsed}: {Stops} halts, "
                + "{Routes} routes, {Patterns} distinct stop patterns.",
                active.FeedVersionId,
                active.FeedInfoVersion ?? "no feed_info version",
                System.Diagnostics.Stopwatch.GetElapsedTime(started),
                feed.Stops.Count,
                feed.Patterns.Select(pattern => pattern.RouteId).Distinct(StringComparer.Ordinal).Count(),
                feed.Patterns.Count);

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Keeps <see cref="IGtfsFeedCache"/> within 60 s of the active feed (D6' I-32.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two triggers, and the second is why the bound holds.</b> <c>LISTEN transit_feed_activated</c>
/// is what makes a reload near-instant; the poll is what makes it <em>guaranteed</em>. A
/// notification is delivered to sessions that are connected at the moment it fires — a reconnect
/// window, a dropped connection, a PgBouncer in transaction mode — so a service that only listened
/// would serve the previous feed until something else woke it, with nothing to say it had.
/// </para>
/// <para>
/// <b>The direct connection is deliberate.</b> PgBouncer in transaction mode returns the session to
/// the pool between statements and the <c>LISTEN</c> registration goes with it — the same reason
/// the kernel's outbox dispatcher takes <c>OpenDirectAsync</c>.
/// </para>
/// </remarks>
internal sealed class GtfsFeedListener : BackgroundService
{
    private readonly IGtfsFeedCache _cache;
    private readonly MageRide.Shared.Persistence.INpgsqlConnectionFactory _connections;
    private readonly TransitOptions _options;
    private readonly ILogger<GtfsFeedListener> _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);

    public GtfsFeedListener(
        IGtfsFeedCache cache,
        MageRide.Shared.Persistence.INpgsqlConnectionFactory connections,
        IOptions<TransitOptions> options,
        ILogger<GtfsFeedListener> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary><see langword="true"/> while a direct connection holds an active LISTEN.</summary>
    internal volatile bool IsListening;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GTFS feed cache starting: LISTEN {Channel}, safety-net poll every {Interval}.",
            _options.FeedChannel, _options.FeedPollInterval);

        await Task.WhenAll(ListenAsync(stoppingToken), RefreshLoopAsync(stoppingToken));
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var listen = $"LISTEN {QuoteIdentifier(_options.FeedChannel)};";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _connections.OpenDirectAsync(stoppingToken);

                connection.Notification += OnNotification;

                try
                {
                    await using (var command = new NpgsqlCommand(listen, connection))
                    {
                        await command.ExecuteNonQueryAsync(stoppingToken);
                    }

                    IsListening = true;

                    // A feed activated while this service was reconnecting is still unloaded.
                    Signal();

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await connection.WaitAsync(stoppingToken);
                    }
                }
                finally
                {
                    IsListening = false;
                    connection.Notification -= OnNotification;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "LISTEN on {Channel} dropped; reconnecting. The safety-net poll keeps the cache inside "
                    + "its {Interval} bound meanwhile.",
                    _options.FeedChannel, _options.FeedPollInterval);

                await Delay(_options.FeedPollInterval, stoppingToken);
            }
        }
    }

    private async Task RefreshLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _cache.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A feed that could not be loaded leaves the previous one published, which is the
                // right failure: yesterday's routes beat no routes, and the poll comes back.
                _logger.LogError(exception, "Reloading the GTFS feed failed; the previous feed stays published.");
            }

            try
            {
                await _signal.WaitAsync(_options.FeedPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs args)
    {
        _logger.LogInformation("{Channel} fired; reloading the GTFS feed.", args.Channel);

        Signal();
    }

    /// <summary>A latch, not a queue: one pending wake-up is enough.</summary>
    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    private async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Quotes a channel name, because it is configuration and cannot be a parameter.</summary>
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
