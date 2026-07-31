using System.Reflection;
using MageRide.Voip.Domain;
using MageRide.Voip.Signalling;

namespace MageRide.Voip.Tests.Unit;

/// <summary>
/// <b>Definition of done: "no code path provisions a masked DID or sends a masked SMS relay."</b>
/// </summary>
/// <remarks>
/// <para>
/// Asserted by reflection over the whole assembly rather than by reading it, because the thing being
/// prevented is a <em>future</em> addition. AL-48 withdrew number masking as a product decision and
/// three earlier documents still describe it — D3' Δ 2026-06-28's `normal_masked` leg, D6' I-28.3's
/// PSTN bridge and I-29.3's proxy-DID lease — so somebody implementing from the wrong section is a
/// realistic way for this to come back. A test that fails on the name is louder than a comment.
/// </para>
/// <para>
/// The phone-number half is the same fence from the other side: AL-48 puts the counterparty's MSISDN
/// on ride-svc's ride detail, and a service that cannot name a phone number cannot leak one.
/// </para>
/// </remarks>
public sealed class MaskingWithdrawnTests
{
    private static readonly Assembly Service = typeof(VoipApplication).Assembly;

    /// <summary>What AL-48 removed. Each is a whole word or a distinctive fragment.</summary>
    private static readonly string[] Withdrawn =
    [
        "masked", "masking", "normalmasked", "webmasked",
        "pstn", "cpaas", "did_pool", "didpool", "proxydid", "smsrelay", "twilio", "exotel", "plivo",
    ];

    /// <summary>
    /// The counterparty's number is ride-svc's to serve, never this service's.
    /// </summary>
    /// <remarks>
    /// <c>callerId</c> is deliberately <b>not</b> on this list even though "caller ID" is a
    /// telephony term for a number: <c>comms.call_log.caller_id</c> is a <c>iam.users</c> foreign
    /// key, and banning the word would ban the column this service has to write.
    /// </remarks>
    private static readonly string[] Numbers = ["phone", "msisdn", "e164", "dialnumber", "dial_number"];

    [Fact]
    public void Nothing_in_this_service_is_named_after_the_withdrawn_masking_stack()
    {
        var offenders = Names()
            .Where(name => Withdrawn.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "AL-48 withdrew number masking: no masked PSTN bridge, no proxy-DID lease, no masked-SMS relay. "
            + $"Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Nothing_in_this_service_can_name_a_phone_number()
    {
        var offenders = Names()
            .Where(name => Numbers.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "\"Normal call\" is a client-side tel: dial of the number ride-svc carries post-accept (AL-48). "
            + $"voip-svc must not hold, serve or store one. Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void The_call_type_vocabulary_is_exactly_the_two_values_AL_48_left()
    {
        Assert.Equal(["free_voip", "direct_dial"], CallTypes.All);
        Assert.False(CallTypes.IsKnown("normal_masked"));
        Assert.False(CallTypes.IsKnown("web_masked"));
    }

    [Fact]
    public void The_only_outbound_client_this_service_has_is_LiveKit()
    {
        // A second named HttpClient would be a second integration, and the only one AL-48 leaves is
        // the SFU's own server API. There is no CPaaS, no DID pool and no operator voice API.
        var clientNames = Service.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)
                            && field.Name.EndsWith("HttpClientName", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal([LiveKitRoomService.HttpClientName], clientNames);
    }

    /// <summary>Every type, member and parameter name in the service assembly.</summary>
    private static IEnumerable<string> Names()
    {
        foreach (var type in Service.GetTypes())
        {
            // Compiler-generated closures and anonymous types carry their captured members' names
            // in mangled form; they are covered by the members they came from.
            if (type.Name.StartsWith('<'))
            {
                continue;
            }

            yield return type.Name;

            const BindingFlags Everything =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly;

            foreach (var member in type.GetMembers(Everything))
            {
                if (member.Name.StartsWith('<'))
                {
                    continue;
                }

                yield return member.Name;

                if (member is MethodBase method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        yield return parameter.Name ?? string.Empty;
                    }
                }
            }
        }
    }
}
