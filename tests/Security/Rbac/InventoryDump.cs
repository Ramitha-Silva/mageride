using System.Text;

namespace MageRide.Security.Tests.Rbac;

/// <summary>
/// Writes the whole endpoint inventory to a file, for the ASVS evidence appendix.
/// </summary>
/// <remarks>
/// <para>
/// Not an assertion and deliberately not one: it is how <c>security/asvs-l2-checklist.md</c>'s
/// V4 evidence table is regenerated after a service gains or loses an endpoint, and how the
/// reviewer reads the fleet's authorization posture in one place instead of across twenty-five
/// route tables. The assertions are in <see cref="RbacProbeTests"/>.
/// </para>
/// <para>
/// Off unless <c>MAGERIDE_SECURITY_DUMP</c> names a path, so an ordinary run writes nothing.
/// <c>security/run-asvs-checks.sh</c> sets it.
/// </para>
/// </remarks>
public sealed class InventoryDump
{
    [Fact]
    public void Write_the_endpoint_inventory_when_asked()
    {
        var path = Environment.GetEnvironmentVariable("MAGERIDE_SECURITY_DUMP");

        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Skip("Set MAGERIDE_SECURITY_DUMP=<path> to write the endpoint inventory.");
            return;
        }

        var text = new StringBuilder();
        text.AppendLine("service\tguard\troute\tdetail");

        foreach (var endpoint in EndpointInventory.All)
        {
            text.Append(endpoint.Service).Append('\t')
                .Append(endpoint.Guard).Append('\t')
                .Append(endpoint.Route).Append('\t')
                .AppendLine(endpoint.Detail);
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text.ToString());
    }
}
