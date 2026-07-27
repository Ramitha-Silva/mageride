using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Http.Idempotency;

/// <summary>
/// Enforces the D3' §0 idempotency contract: an <c>Idempotency-Key</c> on every POST mutation, and
/// a duplicate key replays the original response verbatim (R-14, R-18; ADD §11.13).
/// </summary>
/// <remarks>
/// <para>
/// Sits after authentication (the actor is recorded in the command log) and inside the exception
/// handler (a 5xx must release the key, not pin a failure to it).
/// </para>
/// <para>
/// A service without an <see cref="ICommandLog"/> registered gets the header validation but no
/// replay — <c>AddMageRideIdempotency</c> fails fast rather than degrade silently.
/// </para>
/// </remarks>
public sealed class IdempotencyMiddleware(
    RequestDelegate next,
    IOptions<IdempotencyOptions> options,
    ILogger<IdempotencyMiddleware> logger)
{
    /// <summary>Set on a replayed response so operators can tell a replay from a fresh execution.</summary>
    public const string ReplayHeader = "X-Idempotent-Replay";

    private readonly IdempotencyOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task InvokeAsync(HttpContext context, ICommandLog commandLog)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commandLog);

        if (!AppliesTo(context))
        {
            await next(context);
            return;
        }

        var rawKey = context.Request.Headers[MageRideHeaders.IdempotencyKey].ToString();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            await WriteProblemAsync(context, MageRideErrors.IdempotencyKeyRequired,
                $"POST mutations require an {MageRideHeaders.IdempotencyKey} header (ULID or UUID).");
            return;
        }

        var key = rawKey.Trim();
        if (!IsWellFormed(key))
        {
            await WriteProblemAsync(context, MageRideErrors.IdempotencyKeyInvalid,
                $"{MageRideHeaders.IdempotencyKey} must be {_options.MinKeyLength}-{_options.MaxKeyLength} characters of [A-Za-z0-9_-].");
            return;
        }

        byte[] requestHash;
        try
        {
            requestHash = await ComputeRequestHashAsync(context);
        }
        catch (IOException)
        {
            // EnableBuffering rejects a body past MaxBufferedRequestBytes.
            await WriteProblemAsync(context, MageRideErrors.PayloadTooLarge,
                $"Request body exceeds {_options.MaxBufferedRequestBytes} bytes.");
            return;
        }

        var logKey = new CommandLogKey(
            IdempotencyKey: key,
            Command: DescribeCommand(context),
            RequestHash: requestHash,
            ActorType: context.User.ActorType(),
            ActorId: context.User.SubjectId());

        var reservation = await commandLog.TryReserveAsync(logKey, context.RequestAborted);

        switch (reservation.Outcome)
        {
            case CommandLogOutcome.Replay:
                await ReplayAsync(context, key, reservation.Response!);
                return;

            case CommandLogOutcome.InProgress:
                logger.LogInformation("Idempotency-Key {Key} is still in progress for {Command}", key, logKey.Command);
                await WriteProblemAsync(context, MageRideErrors.IdempotencyInProgress,
                    "The original request with this Idempotency-Key has not completed yet.");
                return;

            case CommandLogOutcome.Mismatch:
                logger.LogWarning("Idempotency-Key {Key} reused with a different payload for {Command}", key, logKey.Command);
                await WriteProblemAsync(context, MageRideErrors.IdempotencyKeyReuse,
                    "This Idempotency-Key was already used for a different request body.");
                return;

            case CommandLogOutcome.Reserved:
            default:
                await ExecuteAndCaptureAsync(context, commandLog, key);
                return;
        }
    }

    private bool AppliesTo(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IdempotencyMetadata>();
        if (metadata is not null)
        {
            return metadata.Required;
        }

        return _options.Methods.Contains(context.Request.Method);
    }

    private bool IsWellFormed(string key)
    {
        if (key.Length < _options.MinKeyLength || key.Length > _options.MaxKeyLength)
        {
            return false;
        }

        foreach (var c in key)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// SHA-256 over method, path, query and body — the identity of the request the key claims.
    /// A second request under the same key that hashes differently is a client bug, not a retry.
    /// </summary>
    private async Task<byte[]> ComputeRequestHashAsync(HttpContext context)
    {
        var request = context.Request;
        request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: _options.MaxBufferedRequestBytes);

        using var sha = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write, leaveOpen: true);

        var prelude = Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}\n{request.QueryString}\n");
        await stream.WriteAsync(prelude, context.RequestAborted);

        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await stream.FlushFinalBlockAsync(context.RequestAborted);
        request.Body.Position = 0;

        return sha.Hash!;
    }

    private static string DescribeCommand(HttpContext context)
    {
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return $"{context.Request.Method} {pattern ?? context.Request.Path.Value}";
    }

    private async Task ExecuteAndCaptureAsync(HttpContext context, ICommandLog commandLog, string key)
    {
        var originalBodyFeature = context.Features.Get<IHttpResponseBodyFeature>()
            ?? throw new InvalidOperationException("The response body feature is missing; idempotent replay cannot capture the response.");
        using var captured = new MemoryStream();

        try
        {
            context.Response.Body = captured;
            try
            {
                await next(context);
            }
            finally
            {
                context.Features.Set(originalBodyFeature);
            }
        }
        catch
        {
            // The command did not produce a recordable response; let the client's retry execute.
            await ReleaseQuietlyAsync(commandLog, key);
            throw;
        }

        var status = context.Response.StatusCode;
        var body = captured.ToArray();

        if (_options.ShouldStore(status))
        {
            if (body.Length <= _options.MaxStoredResponseBytes)
            {
                await commandLog.CompleteAsync(
                    key,
                    new CommandLogResponse(status, body, context.Response.ContentType),
                    CancellationToken.None);
            }
            else
            {
                logger.LogWarning(
                    "Response for Idempotency-Key {Key} is {Size} bytes, above the {Limit}-byte replay cap; not stored",
                    key, body.Length, _options.MaxStoredResponseBytes);
                await ReleaseQuietlyAsync(commandLog, key);
            }
        }
        else
        {
            await ReleaseQuietlyAsync(commandLog, key);
        }

        if (body.Length > 0)
        {
            await context.Response.Body.WriteAsync(body, CancellationToken.None);
        }
    }

    private async Task ReplayAsync(HttpContext context, string key, CommandLogResponse response)
    {
        logger.LogInformation("Replaying stored response for Idempotency-Key {Key} ({Status})", key, response.Status);

        context.Response.Clear();
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength = response.Body.Length;
        context.Response.Headers[ReplayHeader] = "true";

        if (response.Body.Length > 0)
        {
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
        }
    }

    private async Task ReleaseQuietlyAsync(ICommandLog commandLog, string key)
    {
        try
        {
            await commandLog.ReleaseAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A stuck reservation degrades to "retry gets 409 in-progress", which is recoverable;
            // losing the original failure is not.
            logger.LogError(ex, "Failed to release Idempotency-Key {Key}", key);
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, ErrorCode error, string detail)
    {
        context.Response.StatusCode = error.Status;
        context.Response.ContentType = "application/problem+json";

        var problem = MageRideProblem.Create(context, error, detail);
        await context.Response.WriteAsJsonAsync(problem, MageRideJson.Options, "application/problem+json", context.RequestAborted);
    }
}
