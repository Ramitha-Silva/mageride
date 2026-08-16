using System.Globalization;

namespace MageRide.Notification.Sms;

/// <summary>
/// The two phone-number conversions every SMS gateway on this service needs.
/// </summary>
/// <remarks>
/// These lived on <c>NotifyLkSmsGateway</c> until AL-60 removed it, and the Fit SMS gateway, the
/// secondary and the dev logger all called them through that class — so deleting the gateway would
/// have deleted the redaction the logs depend on. They were never Notify.lk's: the platform stores
/// E.164 everywhere and every gateway wants the national form, and no log line should carry a full
/// MSISDN. iam-svc has its own copy for the reason its gateways do — the two services do not
/// reference each other.
/// </remarks>
internal static class SmsPhone
{
    /// <summary><c>+94771234567</c> → <c>94771234567</c>.</summary>
    internal static string ToNationalDigits(string e164) => e164.TrimStart('+');

    /// <summary>Last four digits only. A delivery log is not a place for a full MSISDN.</summary>
    internal static string Redact(string phone) =>
        phone.Length <= 4 ? "****" : string.Create(CultureInfo.InvariantCulture, $"****{phone[^4..]}");
}
