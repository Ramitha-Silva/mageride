using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Shared.Auth;

/// <summary>
/// Serves iam-svc's signing keys to the JWT handler from a 15-minute cache (D-21).
/// </summary>
/// <remarks>
/// <para>
/// <c>Jwt__JwksUrl</c> is a bare JWKS document, not an OIDC discovery document, so the stock
/// <c>ConfigurationManager&lt;OpenIdConnectConfiguration&gt;</c> cannot be pointed at it. This
/// fetches the key set directly and wraps it in the configuration shape the handler expects.
/// </para>
/// <para>
/// The handler calls <see cref="RequestRefresh"/> when a token's <c>kid</c> is not in the cached
/// set, which is how a signing-key rotation (90 days, D7' §13) is picked up inside the cache
/// window. <see cref="JwtOptions.JwksMinimumRefreshInterval"/> bounds how often that can hit
/// iam-svc, so unknown-<c>kid</c> tokens cannot be used to hammer it.
/// </para>
/// </remarks>
public sealed class JwksConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JwtOptions _options;
    private readonly ILogger<JwksConfigurationManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OpenIdConnectConfiguration? _configuration;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;
    private bool _refreshRequested;

    public JwksConfigurationManager(
        HttpClient httpClient,
        JwtOptions options,
        ILogger<JwksConfigurationManager> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.JwksUrl))
        {
            throw new InvalidOperationException("Jwt:JwksUrl is not configured (D7' §4.1 lists it as required).");
        }

        if (_options.RequireHttpsMetadata &&
            !_options.JwksUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Jwt:JwksUrl '{_options.JwksUrl}' is not HTTPS. Set Jwt:RequireHttpsMetadata=false only for local compose.");
        }

        _httpClient.Timeout = _options.JwksFetchTimeout;
    }

    /// <summary>Number of completed fetches. Exposed for the cache-behaviour tests.</summary>
    internal int FetchCount { get; private set; }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var now = _timeProvider.GetUtcNow();

        if (_configuration is { } cached && !IsStale(now))
        {
            return cached;
        }

        await _gate.WaitAsync(cancel);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_configuration is { } stillCached && !IsStale(now))
            {
                return stillCached;
            }

            // Under a forced refresh, honour the minimum interval so a burst of unknown-kid
            // tokens cannot turn into a fetch storm against iam-svc.
            if (_refreshRequested &&
                _configuration is { } current &&
                now - _lastAttemptAt < _options.JwksMinimumRefreshInterval)
            {
                return current;
            }

            _lastAttemptAt = now;

            try
            {
                var keys = await FetchAsync(cancel);
                _configuration = ToConfiguration(keys);
                _fetchedAt = _timeProvider.GetUtcNow();
                _refreshRequested = false;
                FetchCount++;

                _logger.LogInformation("Loaded {KeyCount} signing keys from {JwksUrl}", keys.Keys.Count, _options.JwksUrl);
                return _configuration;
            }
            catch (Exception ex) when (_configuration is not null)
            {
                // Serve the stale set rather than reject every request while iam-svc is down.
                _logger.LogWarning(ex, "JWKS refresh from {JwksUrl} failed; continuing with the cached key set", _options.JwksUrl);
                return _configuration;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marks the cache stale; the next <see cref="GetConfigurationAsync"/> refetches.</summary>
    public void RequestRefresh() => _refreshRequested = true;

    public void Dispose() => _gate.Dispose();

    private bool IsStale(DateTimeOffset now) =>
        _refreshRequested || now - _fetchedAt >= _options.JwksCacheDuration;

    private async Task<JsonWebKeySet> FetchAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(_options.JwksUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var keySet = JsonWebKeySet.Create(json);

        if (keySet.Keys.Count == 0)
        {
            throw new InvalidOperationException($"JWKS at {_options.JwksUrl} contains no keys.");
        }

        return keySet;
    }

    private OpenIdConnectConfiguration ToConfiguration(JsonWebKeySet keySet)
    {
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = _options.Issuer,
            JwksUri = _options.JwksUrl,
            JsonWebKeySet = keySet,
        };

        foreach (var key in keySet.GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        return configuration;
    }
}
