import MageRideShared
import SwiftUI
import UIKit

/// **SCR-DI-029a · the driver's own documents (Δ MCS-28).**
///
/// The one standing placeholder `DriverDestinations` had left, and `mageride://documents` has always
/// pointed here (E-03). Reached from SCR-DI-029's *My documents* row and from a vehicle's row on
/// SCR-DI-026, which are the two places a driver goes looking.
///
/// **This screen exists to work with no connection.** A driver is asked for a licence at a
/// checkpoint, a depot gate or the side of a road, and that is where a screen that needs signal is
/// worth nothing. The images come off disk (§3.17) and the refresh happens behind them; when it
/// fails, the documents stay and a note says the copies are local.
///
/// The §0.4 fences are the store's, not this screen's, with one exception that belongs here: there
/// is **no share sheet and no download**. A driver who needs to send a licence somewhere has the
/// original in their own photo library.
struct DocumentsScreen: View {

    @StateObject private var model: DocumentsModel

    init(model: @autoclosure @escaping () -> DocumentsModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                if model.offline {
                    // A note rather than an error: the documents below are still the right ones to
                    // show, and this screen's whole purpose is the case where there is no network.
                    NoticeCard(symbolName: "wifi.slash", accent: MageRideColor.secondary) {
                        Text("documents_offline".localised)
                            .mageFont(.label)
                    }
                }

                if model.documents.isEmpty && !model.loading {
                    Text("documents_empty".localised)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }

                ForEach(model.documents, id: \.documentId) { document in
                    DocumentCard(document: document)
                }
            }
            .padding(MageRideSpacing.md)
        }
        .navigationTitle("documents_title".localised)
        .task {
            // The cache first, so the screen opens on a document; the refresh runs behind it.
            await model.paintFromCache()
            await model.refresh()
        }
    }
}

/// One document — its kind, whether the copy has aged, and the image itself.
private struct DocumentCard: View {

    let document: CachedDocumentImage

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            Text(documentKindLabel(document.kind).localised)
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            if document.isStale {
                // Shown rather than hidden: yesterday's certificate beats nothing at a checkpoint,
                // and the driver is told which it is. See `DocumentImageCache`.
                Text("documents_stale".localised)
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            if let image = UIImage(data: IosBytesKt.nsDataOf(bytes: document.bytes) as Data) {
                Image(uiImage: image)
                    .resizable()
                    // Fit, not fill: a document is read, and cropping one to a tidy rectangle is how
                    // the expiry date ends up outside the frame.
                    .scaledToFit()
                    .frame(maxWidth: .infinity)
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(MageRideColor.surface)
        .clipShape(RoundedRectangle(cornerRadius: MageRideRadius.card))
    }
}

/// The five `registry.documents.kind` values, as copy.
private func documentKindLabel(_ kind: String) -> String {
    switch kind {
    case DocumentKind.drivingLicense.wire: return "doc_kind_driving_license"
    case DocumentKind.registration.wire: return "doc_kind_registration"
    case DocumentKind.permit.wire: return "doc_kind_permit"
    case DocumentKind.insurance.wire: return "doc_kind_insurance"
    default: return "doc_kind_revenue_license"
    }
}
