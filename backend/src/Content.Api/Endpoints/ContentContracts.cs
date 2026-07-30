namespace MageRide.Content.Endpoints;

// =============================================================================================
// The wire shapes of backend/contracts/content.yaml. The contract wins over this file: it is what
// C012/C013 generate the KMP client from and what C118 asserts the running service against.
//
// `public` rather than `internal` for the response types, because the test suite deserialises them
// — the same reason Query.Api's contracts are public. The request bodies are public for model
// binding.
// =============================================================================================

/// <summary>A trilingual field as it arrives on the wire. Validated into a domain value.</summary>
/// <remarks>
/// Every member is nullable and that is on purpose: a request body is untrusted, so the type that
/// binds it must be able to *hold* the invalid shapes in order to reject them with a field-level
/// message. <c>TrilingualText.Require</c> is the only door from here into the domain.
/// </remarks>
public sealed record TrilingualTextBody(string? Si, string? Ta, string? En)
{
    internal Dictionary<string, string?> ToMap() => new(StringComparer.Ordinal)
    {
        ["si"] = Si,
        ["ta"] = Ta,
        ["en"] = En,
    };
}

/// <summary>`GET /v1/config/cities` (AL-27).</summary>
public sealed record CitiesResponse(IReadOnlyList<OperatingCityResponse> Cities);

/// <summary>D3' <c>OperatingCity</c>.</summary>
public sealed record OperatingCityResponse(
    string Code,
    string NameEn,
    string NameSi,
    string NameTa,
    CentroidResponse Centroid,
    int SortOrder);

/// <summary>The shared <c>GeoPoint</c> shape — <c>{"lat":…,"lng":…}</c> (D6' §2.2).</summary>
public sealed record CentroidResponse(double Lat, double Lng);

/// <summary>`GET /v1/content/onboarding/{audience}` (AL-28).</summary>
public sealed record OnboardingResponse(IReadOnlyList<OnboardingSlideResponse> Slides);

/// <summary>
/// One carousel slide, in all three languages at once — the picker is on the same screen.
/// </summary>
public sealed record OnboardingSlideResponse(
    int Slot, string IllustrationRef, TrilingualTextBody Title, TrilingualTextBody Body);

/// <summary>`GET /v1/content/templates/{key}` (D-26).</summary>
public sealed record NotificationTemplateResponse(
    string Key,
    string Language,
    int Version,
    string? Title,
    string Body,
    IReadOnlyList<string> Placeholders);

/// <summary>`GET /v1/content/faq`.</summary>
public sealed record FaqResponse(string Language, IReadOnlyList<FaqArticleResponse> Items);

/// <summary>One FAQ article in the resolved language (US-16.1).</summary>
public sealed record FaqArticleResponse(
    Guid ArticleId, string Category, string Title, string Body, int SortOrder);

/// <summary>`GET /v1/content/broadcasts` (US-14.8).</summary>
public sealed record BroadcastsResponse(IReadOnlyList<BroadcastResponse> Items);

/// <summary>One announcement, resolved into the requested language.</summary>
public sealed record BroadcastResponse(
    Guid BroadcastId,
    string Message,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    BroadcastAudienceBody? Audience);

/// <summary>Who an announcement is for. Only the two facts a bearer carries.</summary>
public sealed record BroadcastAudienceBody(string? Role, string? App);

/// <summary>`POST /v1/admin/content/broadcasts`.</summary>
public sealed record PublishBroadcastBody(
    TrilingualTextBody? MessageByLang,
    BroadcastAudienceBody? Audience,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);

/// <summary>`PUT /v1/admin/content/{key}`.</summary>
public sealed record UpdateTemplateBody(TrilingualTextBody? BodyByLang, TrilingualTextBody? TitleByLang);

/// <summary>`POST /v1/admin/content/{key}/approve`.</summary>
public sealed record ApproveTemplateBody(int? Version);

/// <summary>What an admin write did.</summary>
public sealed record TemplateVersionRefResponse(
    string Key, int Version, string Status, DateTimeOffset? ApprovedAt);

/// <summary>`GET /v1/admin/content/{key}` — the version history behind the approval workflow.</summary>
public sealed record TemplateHistoryResponse(
    string Key, int? Current, IReadOnlyList<TemplateVersionResponse> Versions);

/// <summary>One version, all three languages together.</summary>
public sealed record TemplateVersionResponse(
    int Version,
    string Status,
    TrilingualTextBody? TitleByLang,
    TrilingualTextBody BodyByLang,
    IReadOnlyList<string> Placeholders,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    DateTimeOffset CreatedAt);

/// <summary>`POST /v1/internal/content/cache/purge`.</summary>
public sealed record PurgeCacheBody(IReadOnlyList<string>? Datasets);

/// <summary>What was dropped.</summary>
public sealed record PurgeCacheResponse(IReadOnlyList<string> Purged);
