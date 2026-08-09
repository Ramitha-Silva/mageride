using MageRide.Contract.Tests.Model;

namespace MageRide.Contract.Tests.Runtime;

/// <summary>
/// Every operation in every contract document, held against the route tables of the services that
/// serve them — in both directions.
///
/// <para>
/// This is C118's first definition-of-done item at its structural floor: <b>the operation exists on
/// a running service</b>. It is read off the composed pipeline rather than off source, so a route
/// that is written but never mapped, mapped under a different path, or mapped on a different verb
/// fails here — and so does the opposite, a route a service serves that no contract describes, which
/// is a direction a request-driven sweep is blind to by construction.
/// </para>
///
/// <para>
/// <b>A document is not a service, and the join has to allow for that.</b> `fleet.yaml` declares the
/// Epic 23 Mode B paths that <em>subscription-svc</em> answers, and the header of `fleet-health.yaml`
/// and `fleet-billing.yaml` says the same of theirs: those documents exist because the paths sit
/// under another service's prefix at the gateway, not because fleet-svc serves them. So the question
/// asked here is "is this operation mapped by <em>some</em> service in the fleet", which is the
/// question the edge actually answers, and the failure message names where it looked.
/// </para>
/// </summary>
public sealed class RouteTableTests
{
    private static readonly ContractSet Contracts = ContractSet.Current;

    /// <summary>Every route every composed service serves, with the service that serves it.</summary>
    private static readonly Lazy<IReadOnlyDictionary<ServiceRoute, IReadOnlyList<string>>> Served = new(() =>
    {
        var index = new Dictionary<ServiceRoute, List<string>>();

        foreach (var service in ServiceCatalog.All)
        {
            foreach (var route in ServiceRoutes.Of(service))
            {
                if (!index.TryGetValue(route, out var owners))
                {
                    index[route] = owners = [];
                }

                owners.Add(service.Document);
            }
        }

        return index.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<string>)entry.Value);
    });

    public static TheoryData<string> Services()
    {
        var data = new TheoryData<string>();
        foreach (var service in ServiceCatalog.All)
        {
            data.Add(service.Document);
        }

        return data;
    }

    public static TheoryData<string> Documents()
    {
        var data = new TheoryData<string>();
        foreach (var document in Contracts.ServiceDocuments)
        {
            data.Add(document);
        }

        return data;
    }

    [Fact]
    public void The_catalog_covers_every_contract_document()
    {
        // The denominator. A twenty-sixth document added without a service entry would otherwise
        // reduce this whole suite's coverage silently, which is the failure mode a coverage report
        // is supposed to prevent rather than exhibit.
        var documents = Contracts.ServiceDocuments.ToHashSet(StringComparer.Ordinal);
        var composed = ServiceCatalog.All.Select(static service => service.Document).ToHashSet(StringComparer.Ordinal);
        var excused = ServiceCatalog.NotComposed.Keys.ToHashSet(StringComparer.Ordinal);

        var uncovered = documents.Except(composed).Except(excused).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            uncovered.Count == 0,
            $"{uncovered.Count} contract document(s) have no service in `ServiceCatalog` and no entry in "
            + $"`NotComposed`: {string.Join(", ", uncovered)}");

        var stale = composed.Concat(excused).Except(documents).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, $"`ServiceCatalog` names document(s) that do not exist: {string.Join(", ", stale)}");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_operation_the_contract_declares_is_mapped(string document)
    {
        if (ServiceCatalog.NotComposed.ContainsKey(document))
        {
            Assert.SkipWhen(true, ServiceCatalog.NotComposed[document]);
            return;
        }

        var missing = Contracts.Operations
            .Where(operation => operation.Document == document)
            .Where(operation => !RouteDrift.UnmappedOperations.Contains(operation.OperationId))
            .Select(static operation =>
                new ServiceRoute(operation.Method, ServiceRoutes.Normalise(operation.Template)))
            .Distinct()
            .Where(route => !Served.Value.ContainsKey(route))
            .OrderBy(static route => route.ToString(), StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{document}: {missing.Count} contract operation(s) are mapped by no service in the fleet. "
            + "The contract wins — the service is wrong, or a micro-change-set is owed:\n  "
            + string.Join("\n  ", missing));
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void Every_route_the_service_maps_is_in_the_contract(string document)
    {
        var service = ServiceCatalog.All.Single(entry => entry.Document == document);

        // The union of every document, not this one's: see the class remark — several services
        // answer paths another service's document declares.
        var declared = Contracts.Operations
            .Select(static operation =>
                new ServiceRoute(operation.Method, ServiceRoutes.Normalise(operation.Template)))
            .ToHashSet();

        var undocumented = ServiceRoutes.Of(service)
            .Where(static route => !ServiceRoutes.IsOutsideTheOpenApiSurface(route))
            .Where(route => !declared.Contains(route))
            .Where(route => !RouteDrift.UndocumentedRoutes.Contains($"{document} {route}"))
            .Distinct()
            .OrderBy(static route => route.ToString(), StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undocumented.Count == 0,
            $"{document}: {undocumented.Count} route(s) are served and undocumented. An endpoint with no "
            + "contract is an endpoint no client can be generated for and no reviewer has read:\n  "
            + string.Join("\n  ", undocumented));
    }

    [Fact]
    public void The_recorded_drift_is_still_the_drift_that_was_recorded()
    {
        // The ratchet. Every entry in `RouteDrift` is a finding written up in the C118 handoff; this
        // is what stops the list being used as a place to put a new one. An entry that has been
        // FIXED shows up here too — as a stale exemption — so the list shrinks when the platform
        // does the work.
        var stillUnmapped = RouteDrift.UnmappedOperations
            .Where(id => Contracts.Operations
                .Where(operation => operation.OperationId == id)
                .Select(static operation =>
                    new ServiceRoute(operation.Method, ServiceRoutes.Normalise(operation.Template)))
                .All(route => !Served.Value.ContainsKey(route)))
            .ToHashSet(StringComparer.Ordinal);

        var fixedSince = RouteDrift.UnmappedOperations.Except(stillUnmapped).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            fixedSince.Count == 0,
            $"{fixedSince.Count} recorded gap(s) are mapped now. Delete them from `RouteDrift`: "
            + string.Join(", ", fixedSince));

        var stillServed = ServiceCatalog.All
            .SelectMany(service => ServiceRoutes.Of(service).Select(route => $"{service.Document} {route}"))
            .ToHashSet(StringComparer.Ordinal);

        var goneSince = RouteDrift.UndocumentedRoutes.Except(stillServed).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            goneSince.Count == 0,
            $"{goneSince.Count} recorded undocumented route(s) are not served any more. Delete them from "
            + $"`RouteDrift`: {string.Join(", ", goneSince)}");
    }
}
