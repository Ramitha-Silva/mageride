using System.Net;
using System.Net.Http.Json;
using MageRide.Voip.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Voip.Signalling;

/// <summary>
/// The half of D6' §6's "expiring at trip end" that a token cannot express on its own.
/// </summary>
/// <remarks>
/// <para>
/// A LiveKit token is a <em>join</em> credential: its <c>exp</c> is checked when a participant
/// connects and not afterwards, so a call that started before the trip ended would otherwise run
/// for as long as the two parties kept talking. Closing the room is what actually ends it, and it
/// is also what makes an unexpired token useless — there is nothing left to join.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> A LiveKit that cannot be reached must not stall the consumer that is
/// draining <c>ride.events</c>; the room is left to LiveKit's own empty-room timeout and the
/// failure is logged. The database row is closed either way, because what it records is that the
/// ride ended.
/// </para>
/// </remarks>
public interface ILiveKitRoomService
{
    /// <summary>Whether a real LiveKit is configured behind this.</summary>
    bool IsConfigured { get; }

    /// <summary>Disconnects everybody and deletes the room. Returns whether LiveKit confirmed it.</summary>
    Task<bool> CloseRoomAsync(string roomName, CancellationToken cancellationToken);
}

/// <summary>LiveKit's Twirp server API — `POST /twirp/livekit.RoomService/DeleteRoom`.</summary>
public sealed class LiveKitRoomService : ILiveKitRoomService
{
    /// <summary>The named client the timeout is attached to.</summary>
    public const string HttpClientName = "livekit";

    /// <summary>How long the admin token minted for one teardown lives.</summary>
    private static readonly TimeSpan AdminTokenTtl = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _clients;
    private readonly ILiveKitTokenMinter _tokens;
    private readonly VoipOptions _options;
    private readonly ILogger<LiveKitRoomService> _logger;

    public LiveKitRoomService(
        IHttpClientFactory clients,
        ILiveKitTokenMinter tokens,
        IOptions<VoipOptions> options,
        ILogger<LiveKitRoomService> logger)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConfigured => _tokens.IsConfigured && !string.IsNullOrWhiteSpace(_options.LiveKit.ApiUrl);

    public async Task<bool> CloseRoomAsync(string roomName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomName);

        if (!IsConfigured)
        {
            _logger.LogWarning(
                "LiveKit is not configured, so room {Room} could not be closed at trip end. Any call already "
                + "connected will run until LiveKit's own empty-room timeout (D6' §6).",
                roomName);

            return false;
        }

        try
        {
            var client = _clients.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, "twirp/livekit.RoomService/DeleteRoom")
            {
                Content = JsonContent.Create(new { room = roomName }),
            };

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", _tokens.MintAdminToken(roomName, AdminTokenTtl));

            using var response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Closed LiveKit room {Room} at trip end.", roomName);

                return true;
            }

            // A room that was never created is already closed as far as this service is concerned.
            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                return true;
            }

            _logger.LogWarning(
                "LiveKit answered {Status} closing room {Room}.", (int)response.StatusCode, roomName);

            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "LiveKit could not be reached to close room {Room}.", roomName);

            return false;
        }
    }
}

/// <summary>
/// What a deployment with no LiveKit gets.
/// </summary>
/// <remarks>
/// It says so once per teardown rather than pretending. There is no stub that "succeeds": a room
/// nobody closed is a call that can outlive its ride, which is the one property D6' §6 names.
/// </remarks>
public sealed class UnconfiguredLiveKitRoomService(ILogger<UnconfiguredLiveKitRoomService> logger)
    : ILiveKitRoomService
{
    public bool IsConfigured => false;

    public Task<bool> CloseRoomAsync(string roomName, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "No LiveKit server API is configured (Voip:LiveKit:ApiUrl), so room {Room} was not closed at trip end.",
            roomName);

        return Task.FromResult(false);
    }
}
