using MageRide.Iam.Configuration;
using Microsoft.Extensions.Logging;

namespace MageRide.Iam.Otp;

/// <summary>Delivers a minted OTP to a phone number (ADD §12.1, D7' §4.2).</summary>
public interface IOtpSender
{
    /// <summary>Name of the transport, for logs and diagnostics.</summary>
    string Provider { get; }

    Task SendAsync(string phone, string code, string language, CancellationToken cancellationToken);
}

/// <summary>
/// The dev sender: writes the OTP to the log instead of paying Notify.lk for it.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the walking skeleton runnable — C025's scripted end-to-end run reads the
/// code out of the container log. It also puts a live credential in plaintext in that log, so
/// <c>AddIamServices</c> refuses to start with this provider outside Development unless
/// <see cref="SmsOptions.AllowDevSenderOutsideDevelopment"/> says otherwise (the replica sets it;
/// it runs on synthetic numbers).
/// </para>
/// <para>
/// The real Notify.lk sender, its trilingual templates (D-26) and the D-33 secondary gateway are
/// C026/C051.
/// </para>
/// </remarks>
public sealed class DevLoggingOtpSender(ILogger<DevLoggingOtpSender> logger) : IOtpSender
{
    private readonly ILogger<DevLoggingOtpSender> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public string Provider => SmsOptions.DevProvider;

    public Task SendAsync(string phone, string code, string language, CancellationToken cancellationToken)
    {
        // Deliberately at Information with the code in clear — the whole point of this sender.
        _logger.LogInformation("[dev-sms] OTP for {Phone} ({Language}) is {Code}", phone, language, code);
        return Task.CompletedTask;
    }
}
