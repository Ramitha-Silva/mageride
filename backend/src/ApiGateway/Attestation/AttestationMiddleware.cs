using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.Observability;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MageRide.ApiGateway.Attestation;

/// <summary>
/// D-30 enforcement point. On the operations D3' §0 names sensitive — auth, payments, ride accept,
/// wallet and SOS — the caller must present an <c>X-Attestation</c> header that its platform's
/// verifier accepts; failure is <c>401 attestation-failed</c>. Every other route is untouched.
/// </summary>
internal sealed class AttestationMiddleware(
    RequestDelegate next,
    AttestationPolicy policy,
    IEnumerable<IAttestationVerifier> verifiers,
    ILogger<AttestationMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly AttestationPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ILogger<AttestationMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IReadOnlyDictionary<string, IAttestationVerifier> _verifiers =
        (verifiers ?? throw new ArgumentNullException(nameof(verifiers)))
            .ToDictionary(static v => v.Platform, StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_policy.IsSensitive(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var options = _policy.Current;
        var hasPlatform = context.Request.TryGetClientPlatform(out var platform);
        var mode = ResolveMode(options, platform);

        if (mode == AttestationMode.Disabled)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var failure = await EvaluateAsync(context, options, hasPlatform ? platform : null).ConfigureAwait(false);
        if (failure is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var tags = new KeyValuePair<string, object?>[]
        {
            new("platform", platform ?? "unknown"),
            new("reason", failure),
        };

        if (mode == AttestationMode.Audit)
        {
            GatewayDiagnostics.AttestationAudited.Add(1, tags);
            _logger.LogWarning(
                "Attestation would have rejected {Method} {Path} ({Reason}); forwarding because the mode is Audit.",
                context.Request.Method, context.Request.Path, failure);

            await _next(context).ConfigureAwait(false);
            return;
        }

        GatewayDiagnostics.AttestationRejections.Add(1, tags);
        _logger.LogWarning(
            "401 attestation-failed on {Method} {Path}: {Reason}",
            context.Request.Method, context.Request.Path, failure);

        await GatewayProblem.WriteAsync(
            context,
            MageRideErrors.AttestationFailed,
            "This request must carry a valid X-Attestation header.").ConfigureAwait(false);
    }

    /// <summary>Returns null when the request may proceed, or the log-only failure reason.</summary>
    private async ValueTask<string?> EvaluateAsync(HttpContext context, AttestationOptions options, string? platform)
    {
        if (platform is null)
        {
            // A sensitive mutation always comes from one of our apps, and both send X-Platform.
            // Without it there is no verifier to pick, so there is no way to accept the request.
            return "platform-unknown";
        }

        var token = context.Request.Headers[MageRideHeaders.Attestation].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return "missing-header";
        }

        if (token.Length > options.MaxTokenLength)
        {
            return "oversize-header";
        }

        if (!_verifiers.TryGetValue(platform, out var verifier))
        {
            return "verifier-not-configured";
        }

        var request = new AttestationRequest(platform, token, context.Request.Method, context.Request.Path.ToString());
        var result = await verifier.VerifyAsync(request, context.RequestAborted).ConfigureAwait(false);

        return result.IsValid ? null : result.Reason ?? "rejected";
    }

    private static AttestationMode ResolveMode(AttestationOptions options, string? platform) =>
        platform is not null && options.PlatformModes.TryGetValue(platform, out var perPlatform)
            ? perPlatform
            : options.Mode;
}
