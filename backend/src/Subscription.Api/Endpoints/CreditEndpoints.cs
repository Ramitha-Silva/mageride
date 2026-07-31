using MageRide.Subscriptions.Wallet;

namespace MageRide.Subscriptions.Endpoints;

/// <summary>
/// D3' subscription-svc's credit-transfer and bulk-voucher routes, which forward to wallet-svc.
/// </summary>
/// <remarks>
/// <para>
/// <b>These five operations exist twice in D3' Part 2</b> — once here and once under
/// <c>/v1/wallet/**</c> — and C046 landed the working half, because <c>billing.*</c> has exactly one
/// writer (D-09) and ADD §11.6 draws subscription-svc calling wallet-svc for the balance check and the
/// movement. Reimplementing them would be a second writer of the same money carrying a second copy of
/// the discount arithmetic, the not-self rule, the <c>PENDING</c> claim and the account lock ordering —
/// four invariants that would then have two chances to disagree.
/// </para>
/// <para>
/// <b>So the D3'-spelled routes are real and thin.</b> A driver's app may call either spelling and get
/// the same answer from the same code, which is what a client generated from
/// <c>subscription.yaml</c> needs; one of the two spellings should be retired, and that is a contract
/// decision rather than this component's (raised in the C047 handoff, as C007 raised it first).
/// </para>
/// <para>
/// <b>Unmapped when wallet-svc is not configured</b>, rather than answering 503 on every call: an
/// unroutable path is what the platform already does for a family it cannot serve, and the start-up log
/// names exactly which operations went missing.
/// </para>
/// </remarks>
public static class CreditEndpoints
{
    public static IEndpointRouteBuilder MapCreditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var credit = endpoints.MapGroup("/v1").WithTags("credit").RequireAuthorization();

        // AL-01: any driver holding credit transfers it by Driver ID, moving the exact value with no
        // commission. AL-34: the request names a Driver ID — the QR-scan path was removed, and no body
        // on this surface has a field for a scanned payload.
        credit.MapPost("/subscriptions/credit-transfer/request",
            Forward(HttpMethod.Post, "v1/wallet/credit-transfer/request")).WithName("requestCreditTransfer");

        credit.MapGet("/subscriptions/credit-transfer/pending",
            Forward(HttpMethod.Get, "v1/wallet/credit-transfer/pending")).WithName("listPendingCreditTransfers");

        credit.MapPost("/subscriptions/credit-transfer/{transferId}/approve",
            ForwardWithId(HttpMethod.Post, "v1/wallet/credit-transfer/{0}/approve")).WithName("approveCreditTransfer");

        credit.MapPost("/subscriptions/credit-transfer/{transferId}/reject",
            ForwardWithId(HttpMethod.Post, "v1/wallet/credit-transfer/{0}/reject")).WithName("rejectCreditTransfer");

        credit.MapPost("/transfers/driver",
            Forward(HttpMethod.Post, "v1/wallet/credit-transfer/initiate")).WithName("sendCreditToDriver");

        credit.MapPost("/vouchers/purchase",
            Forward(HttpMethod.Post, "v1/wallet/voucher/purchase")).WithName("purchaseVoucher");

        return endpoints;
    }

    private static Delegate Forward(HttpMethod method, string walletPath) =>
        (HttpContext context, IWalletForwarder forwarder, CancellationToken cancellationToken) =>
            forwarder.ForwardAsync(context, method, walletPath + context.Request.QueryString, cancellationToken);

    /// <remarks>
    /// The path segment is re-parsed as a ULID before it is interpolated, so nothing a caller sends can
    /// reshape the wallet-svc path it is spliced into.
    /// </remarks>
    private static Delegate ForwardWithId(HttpMethod method, string walletPathFormat) =>
        (string transferId, HttpContext context, IWalletForwarder forwarder, CancellationToken cancellationToken) =>
            forwarder.ForwardAsync(
                context,
                method,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    walletPathFormat,
                    RequestIds.Require(transferId, "transferId")),
                cancellationToken);
}
