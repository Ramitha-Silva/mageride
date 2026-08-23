import Foundation
import MageRideShared

/// SCR-DI-029a — the driver's own documents (Δ MCS-28).
///
/// **Cache first, always.** ``refresh()`` goes to the network behind whatever is already drawn, and
/// a failure there leaves the documents on screen and raises ``offline`` instead of replacing them
/// with an error. A screen whose whole purpose is a roadside with no signal must not blank itself
/// when it fails to find one.
@MainActor
final class DocumentsModel: ObservableObject {

    /// The driver's identity documents first, then each vehicle's — the order the query answers in
    /// and the order the screen groups by.
    @Published private(set) var documents: [CachedDocumentImage] = []

    /// Only while the first read of the *cache* is in flight, which is milliseconds. The network
    /// refresh behind it does not raise this: a driver looking at their licence should not watch it
    /// be replaced by a spinner.
    @Published private(set) var loading = true

    /// Whether the refresh failed. Not an error state — the point of this screen is that it works
    /// without a connection — so it is a note beside documents that are still shown.
    @Published private(set) var offline = false

    private let store: DriverDocumentStore

    init(store: DriverDocumentStore) {
        self.store = store
    }

    /// What is on disk, drawn on the frame the screen opens.
    func paintFromCache() async {
        documents = await store.cached()
        loading = false
    }

    /// Re-lists, fetches anything new, and sweeps what has aged out (§0.4 condition 3).
    func refresh() async {
        do {
            documents = try await store.refresh()
            offline = false
        } catch {
            // Deliberately keeps the documents. See the type's own remarks.
            offline = true
        }

        loading = false

        // After the refresh rather than before: the sweep and the fetch both touch the same rows,
        // and dropping an image a moment before re-downloading it is the one ordering that costs a
        // driver bytes for nothing.
        await store.sweep()
    }
}
