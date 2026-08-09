using System.Text.RegularExpressions;
using MageRide.Contract.Tests.Model;
using MageRide.Shared.Http;

namespace MageRide.Contract.Tests.Conventions;

/// <summary>
/// The four platform conventions C118's brief names — <c>Idempotency-Key</c> on POST, problem+json
/// on every error, LKR integer minor units, cursor pagination and the <c>/v1</c> prefix — asserted
/// over the whole contract set.
///
/// <para>
/// Spectral asserts most of these too, and that is not a reason to skip them. The lint runs over
/// YAML and knows nothing about the running platform: it cannot see that the header name in the
/// contract is the one <c>MageRideHeaders</c> spells, that the pagination envelope is the shape
/// <c>CursorPage&lt;T&gt;</c> serialises to, or that a schema uses a keyword the response validator
/// in this suite would silently ignore. Each test below says which half it owns.
/// </para>
/// </summary>
public sealed partial class ConventionTests
{
    private static readonly ContractSet Contracts = ContractSet.Current;

    /// <summary>
    /// R-14/R-18: every mutating POST carries the replay key, or says in the document why it cannot.
    /// </summary>
    /// <remarks>
    /// The exemption is not a loophole to be counted — it is six named routes. `_shared.yaml`'s own
    /// note: an external payment gateway "cannot send our header", so those callbacks dedupe on
    /// `provider_transaction_id` (R-19) instead. The assertion therefore checks the reason is
    /// *stated*, and the companion test below checks the exempt set has not quietly grown.
    /// </remarks>
    [Fact]
    public void Every_post_requires_an_idempotency_key()
    {
        var missing = Contracts.Operations
            .Where(static operation => operation.Method == "POST")
            .Where(static operation => operation.IdempotencyExemptReason is null)
            .Where(static operation => !operation.Parameters.Any(static parameter =>
                parameter.In == ParameterLocation.Header
                && string.Equals(parameter.Name, MageRideHeaders.IdempotencyKey, StringComparison.OrdinalIgnoreCase)
                && parameter.Required))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} POST operation(s) neither require `{MageRideHeaders.IdempotencyKey}` nor declare "
            + $"`x-idempotency-exempt`: {string.Join(", ", missing.Take(10).Select(static o => o.ToString()))}");
    }

    [Fact]
    public void The_idempotency_header_is_the_one_the_kernel_reads()
    {
        // A contract that spelled it `Idempotency-Id` would pass Spectral's shape rule and be
        // ignored by `IdempotencyMiddleware`, which reads `MageRideHeaders.IdempotencyKey`. The
        // shared parameter is the single definition every operation references.
        var name = Contracts.Node(ContractSet.SharedDocument, "components", "parameters", "IdempotencyKey", "name");

        Assert.Equal(MageRideHeaders.IdempotencyKey, name as string);
    }

    /// <summary>
    /// The exemptions this directory carries, and the rule they actually satisfy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>`_shared.yaml`'s own §0 prose is out of date, and this is where that shows.</b> It reads:
    /// "The only exemptions are the six payment-provider callbacks, which are HMAC-authenticated and
    /// dedupe on `provider_transaction_id` (R-19)." The directory carries <b>nineteen</b>, across
    /// five documents — the extra thirteen are internal-plane routes whose natural key is a business
    /// fact rather than a header: a monthly billing run keyed `(vehicleId, periodMonth)`, a daily fee
    /// keyed `(driverId, vehicleId, feeDate)` in Asia/Colombo, a cache purge that "drops an
    /// already-empty cache". Every one of them states its reason. The prose is stale; the documents
    /// are not wrong. Recorded as a micro-change-set candidate in the C118 handoff, because §0 is
    /// D3'-derived and correcting it is a spec change rather than a test's business.
    /// </para>
    /// <para>
    /// So the rule asserted here is the one the reasons all share, and it is stronger than a count:
    /// <b>a bearer-authenticated route is never exempt.</b> The replay key exists for the request a
    /// person's second tap can send, and only a route reached by a person can receive one. An
    /// exemption is admissible on the two surfaces no person holds a session on — an
    /// HMAC-signed provider callback (R-19) and the mTLS internal plane — and nowhere else.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_bearer_authenticated_route_is_exempt_from_the_replay_key()
    {
        // The one exception, and it is argued rather than assumed. `POST /v1/notifications/{id}/ack`
        // races E-01's three-second deadline: "answering `400 idempotency-key-required` to a handset
        // that woke up in time would fire the fallback for a driver who was there." It is idempotent
        // by construction — a guarded UPDATE bound to `Sent`, so a repeat matches nothing — which is
        // the property the replay key would otherwise have supplied.
        const string RacesTheOfferFallbackDeadline = "acknowledgeNotification";

        var wrong = Contracts.Operations
            .Where(static operation => operation.IdempotencyExemptReason is not null)
            .Where(static operation => !operation.Security.Contains("hmacSignature", StringComparer.Ordinal)
                                       && !operation.Security.Contains("mtls", StringComparer.Ordinal))
            .Where(static operation => operation.OperationId != RacesTheOfferFallbackDeadline)
            .ToList();

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} operation(s) skip `Idempotency-Key` on a surface a person's second tap can "
            + $"reach: {string.Join(", ", wrong.Select(static o => $"{o.OperationId} ({string.Join("|", o.Security)})"))}");
    }

    [Fact]
    public void Every_exemption_states_its_reason()
    {
        var silent = Contracts.Operations
            .Where(static operation => operation.IdempotencyExemptReason is not null)
            .Where(static operation => string.IsNullOrWhiteSpace(operation.IdempotencyExemptReason))
            .ToList();

        Assert.True(silent.Count == 0, $"Exempt with no reason: {string.Join(", ", silent)}");
    }

    /// <summary>
    /// The exempt set itself, pinned.
    /// </summary>
    /// <remarks>
    /// A list rather than a count, because an exemption is a route on which a double-tap can take
    /// money twice: adding the twentieth has to be a diff somebody justifies, not a number that
    /// moves. The list is sorted so a rebase reads cleanly.
    /// </remarks>
    [Fact]
    public void The_exempt_routes_are_the_nineteen_that_were_reviewed()
    {
        string[] reviewed =
        [
            "acknowledgeNotification",
            "chargeDailyFeeBeforeTrip",
            "internalFleetWalletAccount",
            "internalFleetWalletCredit",
            "internalFleetWalletDebit",
            "internalWalletCredit",
            "internalWalletDebit",
            "internalWalletDriverPayout",
            "internalWalletDriverPayoutReverse",
            "internalWalletTripPayment",
            "lankaqrFleetTopupConfirm",
            "lankaqrTopupConfirm",
            "modeBLankaqrConfirm",
            "onepayFleetTopupWebhook",
            "onepayTopupWebhook",
            "purgeContentCache",
            "reportPayoutResult",
            "runFleetBilling",
            "runModeBMonthlyCharge",
        ];

        var actual = Contracts.Operations
            .Where(static operation => operation.IdempotencyExemptReason is not null)
            .Select(static operation => operation.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(reviewed, actual);
    }

    /// <summary>
    /// D3' §0 makes **every** error `application/problem+json`, and the three web surfaces read it.
    /// </summary>
    [Fact]
    public void Every_error_response_is_problem_json()
    {
        var wrong = new List<string>();

        foreach (var operation in Contracts.Operations)
        {
            foreach (var response in operation.Responses)
            {
                if (!IsError(response.Status) || response.Content.Count == 0)
                {
                    continue;
                }

                if (!response.Content.Keys.Any(static type =>
                        type.Contains("problem+json", StringComparison.OrdinalIgnoreCase)))
                {
                    wrong.Add($"{operation} → {response.Status} as {string.Join("/", response.Content.Keys)}");
                }
            }
        }

        Assert.True(wrong.Count == 0, $"Error responses that are not problem+json: {string.Join("; ", wrong.Take(10))}");
    }

    /// <summary>
    /// Money is integer minor units, everywhere, in both directions (CLAUDE.md, D3' §0).
    /// </summary>
    /// <remarks>
    /// Two rules, and the second is the one a reviewer misses: a <c>*Minor</c> field must be
    /// <c>integer/int64</c>, **and** a field named for money that is *not* <c>*Minor</c> must not be
    /// a <c>number</c>. The first catches a rupee value typed as a decimal; the second catches a
    /// rupee value that was never named minor in the first place.
    /// </remarks>
    [Fact]
    public void Currency_amounts_are_integer_minor_units()
    {
        var wrong = new List<string>();

        foreach (var (label, schema) in EverySchema())
        {
            foreach (var (name, property) in schema.Properties)
            {
                var types = property.Types;

                if (name.EndsWith("Minor", StringComparison.Ordinal))
                {
                    if (!types.Contains("integer", StringComparer.Ordinal) && types.Count > 0)
                    {
                        wrong.Add($"{label}.{name} is `{string.Join("|", types)}`, not integer");
                    }
                    else if (types.Count > 0 && property.Format != "int64")
                    {
                        wrong.Add($"{label}.{name} is integer with format `{property.Format ?? "(none)"}`, not int64");
                    }
                }
                else if (MoneyName().IsMatch(name) && types.Contains("number", StringComparer.Ordinal))
                {
                    wrong.Add($"{label}.{name} is a `number`; currency crosses the wire as integer minor units");
                }
            }
        }

        Assert.True(wrong.Count == 0, $"Money convention violations:\n  {string.Join("\n  ", wrong.Distinct().Take(20))}");
    }

    [Fact]
    public void Every_currency_field_is_fixed_at_LKR()
    {
        var wrong = new List<string>();

        foreach (var (label, schema) in EverySchema())
        {
            foreach (var (name, property) in schema.Properties)
            {
                if (!string.Equals(name, "currency", StringComparison.Ordinal))
                {
                    continue;
                }

                // Either the shared `Currency` schema (a const) or a local const. A plain string
                // would let a service answer "USD" and satisfy the contract.
                var fixedAtLkr = string.Equals(property.Const, "LKR", StringComparison.Ordinal)
                                 || (property.Enum.Count == 1 && property.Enum[0] == "LKR");

                if (!fixedAtLkr)
                {
                    wrong.Add(label + ".currency");
                }
            }
        }

        Assert.True(wrong.Count == 0, $"`currency` is not fixed at LKR in: {string.Join(", ", wrong.Distinct().Take(20))}");
    }

    /// <summary>The cursor envelope is one shape, and it is the shape the kernel serialises.</summary>
    [Fact]
    public void Cursor_pagination_is_the_shared_envelope()
    {
        var envelope = new ContractSchema(
            Contracts.Node(ContractSet.SharedDocument, "components", "schemas", "CursorPage"),
            ContractSet.SharedDocument);

        Assert.False(envelope.IsEmpty, "`_shared.yaml#/components/schemas/CursorPage` did not resolve.");

        // `cursor` is always present and null on the last page — C002 decision 9, so that "last
        // page" cannot be confused with "field missing". That is only true if the contract puts it
        // in `required` AND admits null.
        Assert.Contains("cursor", envelope.Required);
        Assert.Contains("null", envelope.Properties["cursor"].Types);
    }

    [Fact]
    public void Every_path_carries_a_supported_prefix()
    {
        // `/public` is the AL-44 token-scoped family, "versioned by share-token scope rather than by
        // a path segment" — the one documented exception, and it is one document's worth.
        var wrong = Contracts.Operations
            .Where(static operation =>
                !operation.Template.StartsWith("/v1/", StringComparison.Ordinal)
                && !operation.Template.StartsWith("/public/", StringComparison.Ordinal))
            .ToList();

        Assert.True(wrong.Count == 0, $"Paths outside /v1 and /public: {string.Join(", ", wrong.Take(10))}");
    }

    [Fact]
    public void Every_operation_id_is_unique_within_its_document()
    {
        // An OpenAPI document with two operations of the same id is not a valid document, and the
        // KMP client's per-service surface would have two methods with one name.
        var duplicates = Contracts.Operations
            .GroupBy(static operation => (operation.Document, operation.OperationId))
            .Where(static group => group.Count() > 1)
            .Select(static group => $"{group.Key.Document}: {group.Key.OperationId}")
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate operationIds: {string.Join("; ", duplicates)}");
    }

    /// <summary>
    /// The three operation ids that appear in two documents, and why each is allowed to.
    /// </summary>
    /// <remarks>
    /// The KMP api-client is hand-written (C012/C013), not generated, so a collision across two
    /// documents is not a client that cannot be built — which is what makes this a pinned list
    /// rather than a refusal. All three are <c>admin-bff</c> proxying an operation another service
    /// owns, and sharing the id is the honest spelling of that: it is the same operation, reached
    /// through the console. A <b>fourth</b> collision between two *domain* services would be two
    /// different operations wearing one name, and this test is what makes somebody say which it is.
    /// </remarks>
    [Fact]
    public void Only_the_admin_console_proxies_share_an_operation_id_across_documents()
    {
        string[] proxied =
        [
            "approveDriverPayoutProfile (admin-bff, registry)",
            "listWalletTransactions (admin-bff, wallet)",
            "rejectDriverPayoutProfile (admin-bff, registry)",
        ];

        var shared = Contracts.Operations
            .GroupBy(static operation => operation.OperationId, StringComparer.Ordinal)
            .Where(static group => group.Select(static o => o.Document).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(static group =>
                $"{group.Key} ({string.Join(", ", group.Select(static o => o.Document).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(proxied, shared);
    }

    [Fact]
    public void Every_operation_declares_its_security_explicitly()
    {
        // Deny-by-default is not something a contract may leave to a document-level default: a
        // reader of one operation has to be able to see whether it is public. An empty `security`
        // list is a decision; an absent one is an omission that reads identically.
        var missing = Contracts.Operations
            .Where(operation => Contracts.Node(operation.Document, "paths") is not null)
            .Where(operation => !DeclaresSecurity(operation))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} operation(s) do not declare `security`: {string.Join(", ", missing.Take(10))}");
    }

    /// <summary>
    /// Nothing in the directory uses a schema keyword this suite's validator would ignore.
    /// </summary>
    /// <remarks>
    /// The point of a bespoke validator is that its supported set is written down; the risk of one
    /// is that a contract grows a constraint it does not implement and the constraint is silently
    /// unenforced from then on. This is the test that makes that impossible to do by accident.
    /// </remarks>
    [Fact]
    public void No_schema_uses_a_keyword_the_response_validator_ignores()
    {
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var document in Contracts.Documents)
        {
            if (Contracts.Node(document, "components", "schemas") is not IReadOnlyDictionary<string, object?> schemas)
            {
                continue;
            }

            foreach (var (_, node) in schemas)
            {
                Collect(node, document, unsupported, 0);
            }
        }

        Assert.True(
            unsupported.Count == 0,
            $"Schema keyword(s) `SchemaValidator` does not implement: {string.Join(", ", unsupported)}. "
            + "Either implement them or the constraint is documentation.");
    }

    /* --------------------------------------------------------------------------------------- */

    private static bool DeclaresSecurity(ContractOperation operation)
    {
        if (Contracts.Node(operation.Document, "paths", operation.Template, operation.Method.ToLowerInvariant())
            is not IReadOnlyDictionary<string, object?> node)
        {
            return true;
        }

        return node.ContainsKey("security");
    }

    private static void Collect(object? node, string document, ISet<string> found, int depth)
    {
        if (depth > 16 || node is null)
        {
            return;
        }

        switch (node)
        {
            case IReadOnlyDictionary<string, object?> map:
            {
                foreach (var (key, value) in map)
                {
                    // Keys under `properties` are field names, not keywords, and `enum` values are
                    // data. Descend into the values in both cases; do not judge the keys.
                    if (key is "properties" or "patternProperties" or "definitions" or "$defs")
                    {
                        if (value is IReadOnlyDictionary<string, object?> children)
                        {
                            foreach (var (_, child) in children)
                            {
                                Collect(child, document, found, depth + 1);
                            }
                        }

                        continue;
                    }

                    if (key is "enum" or "example" or "examples" or "default" or "const")
                    {
                        continue;
                    }

                    if (!SchemaValidator.SupportedKeywords.Contains(key))
                    {
                        found.Add(key);
                    }

                    Collect(value, document, found, depth + 1);
                }

                break;
            }

            case IList<object?> list:
            {
                foreach (var item in list)
                {
                    Collect(item, document, found, depth + 1);
                }

                break;
            }

            default:
                break;
        }
    }

    /// <summary>Every schema reachable from every response and request body in the set.</summary>
    private static IEnumerable<(string Label, ContractSchema Schema)> EverySchema()
    {
        foreach (var operation in Contracts.Operations)
        {
            if (operation.RequestBody is { } body)
            {
                foreach (var found in body.Descend($"{operation.OperationId}(body)"))
                {
                    yield return found;
                }
            }

            foreach (var response in operation.Responses)
            {
                foreach (var (_, schema) in response.Content)
                {
                    foreach (var found in schema.Descend($"{operation.OperationId}({response.Status})"))
                    {
                        yield return found;
                    }
                }
            }
        }
    }

    private static bool IsError(string status) =>
        status.Length == 3 && status[0] is '4' or '5';

    [GeneratedRegex("^(amount|fare|price|total|balance|fee|charge|cost|penalty|credit|debit|payout|commission)",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MoneyName();
}
