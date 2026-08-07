import Foundation
import MageRideShared

/// SCR-DI-025's state.
///
/// - Parameters:
///   - filter: Which of the four chips is on.
///   - query: The `.searchable` field's text (Δ iOS).
///   - from: Start of the Colombo date range, or `nil` for "everything the server sends".
///   - to: End of that range.
///   - lines: Every row the server answered with, unfiltered — the chip is applied on read.
///   - isLoading: The read is in flight.
///   - exporting: Which format is being downloaded, or `nil`.
///   - exported: The statement the share sheet is up for.
///   - errorKey: Resolved copy for the last failure.
struct WalletHistoryState {

    var filter = HistoryFilter.all
    var query = ""
    var from: BusinessDate?
    var to: BusinessDate?
    var lines: [WalletTransaction] = []
    var isLoading = true
    var exporting: StatementFormat?
    var exported: StatementFile?
    var errorKey: String?

    /// What the list actually draws.
    var visible: [WalletTransaction] { lines.filter(filter.keeps).filter(matchesQuery) }

    /// D2' §SCR-DI-025's *"empty"* — the read answered and the chip kept nothing.
    var isEmpty: Bool { !isLoading && visible.isEmpty }

    /// Whether a date range has been chosen, which is what the toolbar's calendar reflects.
    var hasRange: Bool { from != nil || to != nil }

    /// The `.searchable` predicate (Δ iOS).
    ///
    /// **Local, over the page already read, and it does not match the amount.**
    /// `GET /v1/wallet/{userId}/transactions` takes a date range and nothing else — no `q`, no `kind` —
    /// so a search that hit the API would be a contract change. What it matches is the row's own
    /// **localised name** and its `reference`, which is what a driver looking for *"the top-up from
    /// Tuesday"* is actually reading; matching the rendered amount as a substring would put every
    /// `Rs 1,000` line under a search for `100`.
    func matchesQuery(_ line: WalletTransaction) -> Bool {
        let needle = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !needle.isEmpty else { return true }

        let haystack = [LedgerKinds.labelKey(for: line.kind).localised, line.reference ?? ""]
        return haystack.contains { $0.localizedCaseInsensitiveContains(needle) }
    }
}

/// **SCR-DI-025 · payment & fee history** (US-9A.6, US-9A.19, D-13).
///
/// The wallet ledger, newest first, over a **Colombo** date range (D-13, D-38) — the same range the
/// statement download uses, so what is on screen and what is in the file are the same rows.
///
/// ### Three filters, and they are not the same kind of thing
///
/// The **date range** is the server's: `GET /v1/wallet/{userId}/transactions` takes `from` and `to` and
/// evaluates them as Colombo business dates. The four **chips** and the `.searchable` **text** are the
/// device's, because that route takes neither a `kind` nor a `q` — adding either would be a contract
/// change. Filtering locally is also why ``WalletHistoryState/lines`` holds everything and `visible` is
/// derived: switching a chip or typing must not re-hit the API.
///
/// ### "Receipt download" is the statement
///
/// wallet-svc declares no per-transaction receipt route. `GET …/transactions` with an `Accept` of
/// `text/csv` or `application/pdf` is the only download in `wallet.yaml`, and US-9A.19 calls it a
/// statement. So the toolbar's download offers those two formats over the range on screen, and a row is
/// not separately downloadable. Recorded as a spec gap in the C073 handoff and carried forward.
@MainActor
final class WalletHistoryModel: ObservableObject {

    @Published private(set) var state = WalletHistoryState()

    private let identity: DriverIdentity
    private let wallet: WalletRepository
    private let exporter: StatementExporter

    init(identity: DriverIdentity, wallet: WalletRepository, exporter: StatementExporter) {
        self.identity = identity
        self.wallet = wallet
        self.exporter = exporter
    }

    /// The `All · Fees · Top-ups · Transfers` chips. Local — no read.
    func select(filter: HistoryFilter) {
        state.filter = filter
    }

    /// The `.searchable` field. Local — no read.
    func onQueryChange(_ raw: String) {
        state.query = raw
    }

    /// The date-range filter. Both bounds are **Asia/Colombo** business dates.
    func setRange(from: BusinessDate?, to: BusinessDate?) async {
        state.from = from
        state.to = to
        await refresh()
    }

    /// Re-reads the ledger for the selected range.
    func refresh() async {
        guard let driverId = identity.driverId else { return }
        state.isLoading = true
        state.errorKey = nil

        do {
            state.lines = try await wallet.transactions(driverId: driverId, from: state.from, to: state.to)
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// US-9A.19's download, in `format`, over the range on screen.
    ///
    /// **The chip and the search text are deliberately not applied**: a statement is evidence of what
    /// the ledger did, and one that quietly omitted every line the driver had filtered out would be a
    /// document that does not reconcile with the balance printed on it.
    func export(_ format: StatementFormat) async {
        guard let driverId = identity.driverId, state.exporting == nil else { return }
        state.exporting = format
        state.exported = nil
        state.errorKey = nil

        do {
            let bytes = try await wallet.statement(
                driverId: driverId,
                format: format,
                from: state.from,
                to: state.to
            )
            if let file = exporter.write(fileName: fileName(format), bytes: bytes) {
                state.exported = StatementFile(url: file)
            } else {
                state.errorKey = "wallet_statement_failed"
            }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.exporting = nil
    }

    /// The share sheet closed.
    func dismissExported() {
        state.exported = nil
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// `mageride-wallet-2026-06-01-2026-06-30.csv`.
    ///
    /// A file name is **data, not copy** — the same rule `Rs` and `+94` follow (C086) — so it is built
    /// here from ISO dates rather than from three `Localizable.strings` entries that would be
    /// byte-identical and fail `LocalizationTests`. `all` stands in for an open bound so two downloads
    /// of different ranges never overwrite each other in the cache.
    /// `String(describing:)` rather than a `DateFormatter`: a `BusinessDate` is a
    /// `kotlinx.datetime.LocalDate` whose `toString()` is already ISO-8601, and it reaches this side of
    /// the bridge as the object's `description`. The Android twin writes `BusinessDate.toString()` and
    /// the two produce the same bytes.
    private func fileName(_ format: StatementFormat) -> String {
        let from = state.from.map { String(describing: $0) } ?? Self.openBound
        let to = state.to.map { String(describing: $0) } ?? Self.openBound
        return "\(Self.filePrefix)-\(from)-\(to).\(format.fileExtension)"
    }

    private static let filePrefix = "mageride-wallet"
    private static let openBound = "all"
}
