using Microsoft.Extensions.Logging;

namespace MageRide.Registry.Onboarding;

/// <summary>
/// The <see cref="IDocumentExtractionClient"/> a deployment gets when ocr-svc (C054) is not wired
/// up: every document comes back unread, so every document step lands <c>pending_review</c> and
/// goes to the Verification Officer.
/// </summary>
/// <remarks>
/// <para>
/// This is the honest failure, not a stub that pretends. AL-27's auto-approval is ocr-svc's
/// verdict; a placeholder that returned confident fields would approve vehicles nobody checked,
/// and one that threw would stop a driver from saving a step whose upload was perfectly good.
/// Returning <see cref="DocumentExtraction.Unavailable"/> keeps the wizard usable, keeps every
/// pending field in the officer queue where D5' §14.1a puts an unextractable document, and makes
/// the missing service visible in exactly one place — the start-up warning below.
/// </para>
/// <para>
/// Registered with <c>TryAddSingleton</c>, so C054 registering a real client first wins.
/// </para>
/// </remarks>
public sealed class UnconfiguredDocumentExtractionClient(ILogger<UnconfiguredDocumentExtractionClient> logger)
    : IDocumentExtractionClient
{
    public Task<DocumentExtraction> ExtractAsync(
        DocumentExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        logger.LogWarning(
            "No ocr-svc client is configured, so {Kind} upload {UploadId} was not extracted. The step will be " +
            "saved as pending_review and routed to the Verification Officer queue (US-2.10).",
            request.Kind, request.UploadId);

        return Task.FromResult(DocumentExtraction.Unavailable);
    }
}
