using System.Globalization;
using MageRide.Shared.Caching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MageRide.ApiGateway.Attestation;

/// <summary>
/// An App Attest key a device registered at attestation time: its P-256 public key in
/// SubjectPublicKeyInfo (DER) form, plus the highest signature counter seen so far.
/// </summary>
public sealed record AttestedKey(string KeyId, byte[] PublicKeyDer, uint Counter);

/// <summary>
/// Where the gateway reads registered App Attest keys from.
/// </summary>
/// <remarks>
/// The gateway never writes a key. Registration — receiving the attestation object, validating
/// Apple's certificate chain and storing the public key against the device — belongs to iam-svc
/// (C026), which already owns <c>iam.devices</c>. The gateway only reads the key it needs to check
/// an assertion, and advances the replay counter.
/// </remarks>
public interface IAttestedKeyStore
{
    ValueTask<AttestedKey?> GetAsync(string keyId, CancellationToken cancellationToken);

    /// <summary>Records a newly observed signature counter. Never decreases a stored counter.</summary>
    ValueTask AdvanceCounterAsync(string keyId, uint counter, CancellationToken cancellationToken);
}

/// <summary>Process-local store. Dev, tests, and any deployment running attestation disabled.</summary>
internal sealed class InMemoryAttestedKeyStore : IAttestedKeyStore
{
    private readonly Dictionary<string, AttestedKey> _keys = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Register(AttestedKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            _keys[key.KeyId] = key;
        }
    }

    public ValueTask<AttestedKey?> GetAsync(string keyId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_keys.GetValueOrDefault(keyId));
        }
    }

    public ValueTask AdvanceCounterAsync(string keyId, uint counter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_keys.TryGetValue(keyId, out var existing) && counter > existing.Counter)
            {
                _keys[keyId] = existing with { Counter = counter };
            }
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Redis-backed store, shared by every gateway replica so the replay counter is global.
/// </summary>
/// <remarks>
/// <b>Spec gap.</b> ADD §9.4's Redis key space has no entry for attested device keys; this
/// component adds <c>attest:appattest:{keyId}</c> (HASH: <c>pk</c> = base64 SPKI, <c>counter</c>)
/// and it needs a micro-change-set into ADD §9.4 alongside <see cref="RedisKeys"/>. <b>C026 writes
/// the <c>pk</c> field</b> when a device completes App Attest registration; the gateway only reads
/// it and moves <c>counter</c> forward.
/// </remarks>
internal sealed class RedisAttestedKeyStore(
    IConnectionMultiplexer redis, ILogger<RedisAttestedKeyStore> logger) : IAttestedKeyStore
{
    internal const string PublicKeyField = "pk";
    internal const string CounterField = "counter";

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    private readonly ILogger<RedisAttestedKeyStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    internal static string Key(string keyId) => "attest:appattest:" + keyId;

    public async ValueTask<AttestedKey?> GetAsync(string keyId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _redis.GetDatabase()
            .HashGetAsync(Key(keyId), [PublicKeyField, CounterField])
            .ConfigureAwait(false);

        if (!entries[0].HasValue)
        {
            return null;
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(entries[0]!);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Attested key {KeyId} holds a public key that is not base64.", keyId);
            return null;
        }

        var counter = entries[1].HasValue
            && uint.TryParse((string?)entries[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored)
                ? stored
                : 0u;

        return new AttestedKey(keyId, publicKey, counter);
    }

    public async ValueTask AdvanceCounterAsync(string keyId, uint counter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Lua rather than HSET: two replicas verifying concurrent assertions must not let the
        // lower counter win and re-open the replay window the counter exists to close.
        const string script =
            """
            local current = tonumber(redis.call('HGET', KEYS[1], ARGV[1]))
            local proposed = tonumber(ARGV[2])
            if current == nil or proposed > current then
              redis.call('HSET', KEYS[1], ARGV[1], proposed)
            end
            return 1
            """;

        await _redis.GetDatabase()
            .ScriptEvaluateAsync(script, [Key(keyId)], [CounterField, counter.ToString(CultureInfo.InvariantCulture)])
            .ConfigureAwait(false);
    }
}
