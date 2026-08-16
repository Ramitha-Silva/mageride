using System.Globalization;

namespace MageRide.Iam.Otp;

/// <summary>
/// The two phone-number conversions every SMS gateway on this service needs.
/// </summary>
/// <remarks>
/// <para>
/// These lived on <c>NotifyLkOtpSender</c> until AL-60 removed it, and every other sender called
/// them through that class — so deleting the gateway would have deleted the redaction the logs
/// depend on. They were never Notify.lk's: the platform stores E.164 everywhere and every gateway
/// worth the name wants the national form, and no log line anywhere should carry a full MSISDN.
/// </para>
/// </remarks>
internal static class SmsPhone
{
    /// <summary>
    /// <c>+94771234567</c> → <c>94771234567</c>.
    /// </summary>
    /// <remarks>
    /// Fit SMS's <c>recipient</c> is the national form without a <c>+</c>, which is also what
    /// Notify.lk wanted. The platform stores E.164 everywhere else, so the conversion happens at
    /// the boundary that wants the other spelling rather than in the store.
    /// </remarks>
    internal static string ToNationalDigits(string e164) => e164.TrimStart('+');

    /// <summary>Last four digits only. An OTP log line is not a place for a full MSISDN.</summary>
    internal static string Redact(string phone) =>
        phone.Length <= 4 ? "****" : string.Create(CultureInfo.InvariantCulture, $"****{phone[^4..]}");
}
