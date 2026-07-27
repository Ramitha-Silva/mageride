namespace MageRide.Shared.Errors;

/// <summary>
/// One entry in the MageRide error-code registry (D3' §0 "Errors").
/// <para>
/// <paramref name="Code"/> is the stable kebab key that appears in the RFC 7807 <c>type</c> URI
/// (<c>https://mageride.lk/errors/{code}</c>). It is part of the public API contract: clients
/// branch on it, so a code is never renamed or re-purposed once shipped — a superseded code is
/// left in place and a new one added.
/// </para>
/// </summary>
/// <param name="Code">Stable kebab-case key, e.g. <c>offer-expired</c>.</param>
/// <param name="Status">HTTP status this code always maps to.</param>
/// <param name="Title">Short, human-readable, English summary. Not localised — the RFC 7807
/// <c>title</c> is for developers; user-facing copy is resolved from the code by the apps
/// (trilingual resources, CLAUDE.md).</param>
public sealed record ErrorCode(string Code, int Status, string Title)
{
    /// <summary>Absolute <c>type</c> URI for this code, per D3' §0.</summary>
    public string TypeUri => MageRideErrors.TypeUriBase + Code;

    public override string ToString() => $"{Status} {Code}";
}
