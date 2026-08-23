import Foundation
import MageRideShared

/// The driver's own documents, on disk and refreshed behind them (Δ MCS-28).
///
/// **This is the §0.4 identity-document exception in an app**, so the fences it is responsible for
/// are worth naming at the seam rather than only in the spec: nothing here leaves the sandbox, the
/// bytes go into the encrypted database and nowhere else, and ``documentRetention`` is the local
/// lifetime that makes holding them proportionate.
///
/// The reason for holding them at all is the whole of MCS-28: the moment a driver is asked for a
/// licence or an insurance certificate is the moment they are least likely to have a connection.
protocol DriverDocumentStore: AnyObject {

    /// What is on disk now — drawn first, on the frame the screen opens.
    func cached() async -> [CachedDocumentImage]

    /// Lists the driver's documents, fetches any image this handset does not hold, and answers what
    /// is on disk afterwards so a caller draws one list rather than merging two.
    func refresh() async throws -> [CachedDocumentImage]

    /// Drops anything past its local deadline (§0.4 condition 3).
    func sweep() async
}

/// ``DriverDocumentStore`` over §3.17 and registry-svc.
final class ApiDriverDocumentStore: DriverDocumentStore {

    private let registry: RegistryApi
    private let databases: DriverDatabase

    init(registry: RegistryApi, databases: DriverDatabase) {
        self.registry = registry
        self.databases = databases
    }

    func cached() async -> [CachedDocumentImage] {
        guard let cache = await cache() else { return [] }

        return cache.all(now: nowTimestamp())
    }

    func refresh() async throws -> [CachedDocumentImage] {
        let listed = try await registry.listDriverDocuments().items

        // Fetched one at a time rather than concurrently, deliberately. These are photographs on a
        // connection that is a Colombo bus at 7am, and four parallel image downloads on it is how a
        // screen that was merely slow becomes a screen that times out.
        for document in listed {
            await fetchIfMissing(document)
        }

        return await cached()
    }

    func sweep() async {
        await cache()?.forgetExpired(now: nowTimestamp())
    }

    private func fetchIfMissing(_ document: DriverDocument) async {
        guard let cache = await cache() else { return }

        let query = signedLinkParameters(document.imageUrl)
        let version = query["v"]

        // One condition, because the answer to every part of it is the same: leave what is on disk
        // alone. `needs` is asked last, so a link this build cannot parse costs no database read.
        guard let driverId = query["d"],
              let expires = query["expires"].flatMap(Int64.init),
              let signature = query["signature"],
              cache.needs(documentId: document.docId, version: version)
        else { return }

        guard let bytes = try? await registry.getDriverDocumentImage(
            documentId: document.docId,
            driverId: driverId,
            version: version ?? "",
            expires: expires,
            signature: signature)
        else { return }

        cache.write(
            image: DocumentImageWrite(
                documentId: document.docId,
                vehicleId: document.vehicleId,
                kind: document.kind.wire,
                // The contract carries no side on this row: the licence's two images are two
                // documents of the same kind, and which is which is the officer queue's question
                // rather than this screen's.
                side: nil,
                contentType: documentContentType,
                bytes: bytes,
                version: version),
            now: nowTimestamp())
    }

    private func cache() async -> DocumentImageCache? {
        guard let db = await databases.get() else { return nil }

        // `documentImageCacheOf`, not the constructor: `DocumentImageCache` takes a Kotlin
        // `Duration`, which Swift cannot make — see that helper's own remarks.
        return documentImageCacheOf(db: db, retentionMillis: documentRetention)
    }
}

/// How long a cached document image lives on the handset (§0.4 condition 3, Δ MCS-28).
///
/// **No spec fixes this number, and it is deliberately far shorter than NFR-28's ninety days.** That
/// window governs the *server's* copy, which is evidence; this is a convenience copy on a device
/// somebody can lose. Thirty days is long enough that a driver who has not opened the app in a few
/// weeks still has their licence at a checkpoint, and short enough that a handset which stops being
/// used stops holding an NIC fairly quickly.
///
/// Kept in step with the Android twin's `DOCUMENT_RETENTION` by hand — this is a policy number, and
/// two platforms disagreeing about how long an NIC lives on a phone is not a difference anybody
/// would intend.
let documentRetention: Int64 = 30 * 24 * 60 * 60 * 1000

/// What the platform is told these bytes are.
///
/// Every document on this surface arrives as a photograph taken on a handset or a scan of one. A
/// stored content type exists so a viewer does not have to guess, not because this app can offer a
/// better answer than the one it was sent.
private let documentContentType = "image/jpeg"

/// The query of a signed link, as a dictionary (Δ MCS-28).
///
/// `getDriverDocumentImage` takes its four arguments typed, and the only place they exist is inside
/// the URL the list handed back. Deliberately tolerant: a link missing a parameter yields a
/// dictionary missing a key, and the caller answers that by not fetching rather than by throwing at
/// a driver.
func signedLinkParameters(_ url: String) -> [String: String] {
    guard let query = url.split(separator: "?", maxSplits: 1).last, url.contains("?") else { return [:] }

    return query.split(separator: "&").reduce(into: [:]) { result, pair in
        let parts = pair.split(separator: "=", maxSplits: 1)

        guard parts.count == 2 else { return }

        result[String(parts[0])] = String(parts[1])
    }
}
