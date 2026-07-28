using System.Collections.Concurrent;
using MageRide.Iam.Otp;

namespace MageRide.Iam.Tests.Infrastructure;

/// <summary>
/// Stands in for the SMS gateway and keeps what it was asked to send.
/// </summary>
/// <remarks>
/// The dev sender writes the code to the log, which a test could scrape — but reading a log to
/// drive an assertion couples the test to a message format. This captures the same call the dev
/// sender receives; <c>OtpSenderSelectionTests</c> covers the dev sender itself.
/// </remarks>
internal sealed class CapturingOtpSender : IOtpSender
{
    private readonly ConcurrentQueue<SentOtp> _sent = new();

    public string Provider => "capturing";

    /// <summary>Everything sent so far, oldest first.</summary>
    public IReadOnlyList<SentOtp> Sent => [.. _sent];

    /// <summary>The most recent code for a number, or a failure if nothing was sent to it.</summary>
    public string LastCodeFor(string phone)
    {
        var match = _sent.LastOrDefault(s => string.Equals(s.Phone, phone, StringComparison.Ordinal));
        Assert.NotNull(match);
        return match.Code;
    }

    public int CountFor(string phone) => _sent.Count(s => string.Equals(s.Phone, phone, StringComparison.Ordinal));

    public Task SendAsync(string phone, string code, string language, CancellationToken cancellationToken)
    {
        _sent.Enqueue(new SentOtp(phone, code, language));
        return Task.CompletedTask;
    }
}

internal sealed record SentOtp(string Phone, string Code, string Language);
