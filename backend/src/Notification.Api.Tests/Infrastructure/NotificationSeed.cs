using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using MageRide.Notification.Messaging;
using MageRide.Shared.Http;
using MageRide.TestKit;

namespace MageRide.Notification.Tests.Infrastructure;

/// <summary>An account this service can address.</summary>
internal sealed record SeededUser(Guid Id, string Phone, string Language);

/// <summary>
/// The rows other bounded contexts own that this service reads.
/// </summary>
/// <remarks>
/// Written with SQL rather than by calling iam-svc and ride-svc: this suite is about what
/// notification-svc does with an account and a location request, and standing up two more services
/// to create them would make it fail for reasons that are not this component's. Every column set
/// here is one a real service writes.
/// </remarks>
internal sealed class NotificationSeed(PostgresFixture postgres)
{
    private readonly PostgresFixture _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));

    private int _phoneCounter;

    /// <summary>An account with a language and a number.</summary>
    public async Task<SeededUser> UserAsync(
        string language = "en", string? phone = null, string role = "passenger", bool blocked = false)
    {
        var id = Guid.NewGuid();
        var number = phone ?? $"+9477{Interlocked.Increment(ref _phoneCounter):D7}";

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role, first_name, language, is_blocked)
            VALUES (@Id, @Phone, @Role, 'Test', @Language, @Blocked);
            """,
            new { Id = id, Phone = number, Role = role, Language = language, Blocked = blocked });

        return new SeededUser(id, number, language);
    }

    /// <summary>A registered handset. Returns the token row's id.</summary>
    public async Task<Guid> DeviceAsync(Guid userId, string platform = "android", string? token = null)
    {
        var id = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO comms.notification_tokens (id, user_id, platform, token, device_id, last_seen_at)
            VALUES (@Id, @UserId, @Platform, @Token, @DeviceId, now());
            """,
            new
            {
                Id = id,
                UserId = userId,
                Platform = platform,
                Token = token ?? $"tok-{Guid.NewGuid():n}",
                DeviceId = $"dev-{Guid.NewGuid():n}"[..16],
            });

        return id;
    }

    /// <summary>Sets <c>iam.users.notif_prefs</c> the way iam-svc's profile route would.</summary>
    public async Task MuteAsync(Guid userId, string notificationType)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE iam.users SET notif_prefs = notif_prefs || @Patch::jsonb WHERE id = @Id;",
            new { Id = userId, Patch = $"{{\"{notificationType}\":false}}" });
    }

    /// <summary>
    /// A <c>rides.location_requests</c> row in the <c>RiderNotRegistered</c> state, as ride-svc
    /// writes one. Returns the public handle; the surrogate id is what the token points at.
    /// </summary>
    public async Task<Guid> LocationRequestAsync(Guid bookerId, string state = "RiderNotRegistered")
    {
        var requestId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO rides.location_requests (request_id, booker_id, rider_phone_hash, state, ttl_seconds)
            VALUES (@RequestId, @BookerId, decode('00', 'hex'), @State, 300);
            """,
            new { RequestId = requestId, BookerId = bookerId, State = state });

        return requestId;
    }
}

/// <summary>
/// Builds the <see cref="EventEnvelope"/> a consumer would hand a handler.
/// </summary>
/// <remarks>
/// <b>Through the real parser, not around it.</b> The <c>eventType</c> header is where two of the
/// four producers put the event name (registry-svc and wallet-svc serialise their payload with no
/// envelope), so a test that constructed an envelope directly would skip the one decode that has to
/// work for those topics at all.
/// </remarks>
internal static class EventEnvelopeFactory
{
    public static EventEnvelope Build(string key, string eventType, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload, MageRideJson.StorageOptions);

        var message = new Message<string, byte[]>
        {
            Key = key,
            Value = Encoding.UTF8.GetBytes(json),
            Headers = [],
        };

        message.Headers.Add("eventType", Encoding.UTF8.GetBytes(eventType));

        var result = new ConsumeResult<string, byte[]> { Message = message };

        return EventEnvelope.TryParse(result)
               ?? throw new InvalidOperationException($"The test envelope for {eventType} did not parse.");
    }
}
