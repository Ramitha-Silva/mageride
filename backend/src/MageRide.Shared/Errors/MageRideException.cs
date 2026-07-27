namespace MageRide.Shared.Errors;

/// <summary>
/// Throw this to end a request with a registry error code. <see cref="ProblemDetailsExceptionHandler"/>
/// turns it into the RFC 7807 response; nothing else needs to know about it.
/// </summary>
public class MageRideException : Exception
{
    public MageRideException(ErrorCode error, string? detail = null, Exception? innerException = null)
        : base(detail ?? error?.Title, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
        Detail = detail;
    }

    /// <summary>The registry entry that determines status and <c>type</c>.</summary>
    public ErrorCode Error { get; }

    /// <summary>Human-readable, request-specific explanation. Never contains PII.</summary>
    public string? Detail { get; }

    /// <summary>Extra RFC 7807 members to merge into the response.</summary>
    public Dictionary<string, object?> Extensions { get; } = [];

    public MageRideException WithExtension(string key, object? value)
    {
        Extensions[key] = value;
        return this;
    }
}

/// <summary>400 <c>validation-failed</c> with a field → messages map.</summary>
public sealed class MageRideValidationException(IReadOnlyDictionary<string, string[]> errors, string? detail = null)
    : MageRideException(MageRideErrors.ValidationFailed, detail)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? throw new ArgumentNullException(nameof(errors));
}
