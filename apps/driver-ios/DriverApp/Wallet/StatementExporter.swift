import Foundation
import SwiftUI
import UIKit

/// Where SCR-DI-025's statement download actually goes (US-9A.19).
///
/// A seam rather than a call inside the model: writing a file needs a filesystem, and what this
/// component asserts is that the right bytes, the right media type and the right file name reach it.
///
/// **Δ iOS — writing and sharing are two steps here, where Android's exporter is one.**
/// `AndroidStatementExporter` writes the file *and* starts the chooser, because on that platform a
/// chooser is an `Intent` an application context can launch. A share sheet on this platform is a
/// `UIActivityViewController` a **view** presents, anchored to the control the driver tapped — so the
/// exporter's job ends at a `URL` and ``ActivityView`` presents it. That also removes the failure mode
/// the Android seam has to report: there is no "nothing on the handset can receive it", because the
/// share sheet is the system's.
///
/// **Cache plus a share sheet, not the Files app.** Writing into the driver's own documents would need
/// `UIFileSharingEnabled` and a place in Files nobody asked for, for a file whose whole purpose is to
/// be handed to something else. The sheet lets the driver put it wherever they keep documents.
protocol StatementExporter: AnyObject {

    /// Writes `bytes` where the share sheet can reach them.
    ///
    /// - Returns: the file, or `nil` when it could not be written.
    func write(fileName: String, bytes: Data) -> URL?
}

/// ``StatementExporter`` over the app's own caches directory.
final class FileStatementExporter: StatementExporter {

    private let fileManager: FileManager

    init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
    }

    /// Replaces any previous statement of the same name.
    ///
    /// Re-exporting the same range must not accumulate copies in the cache, and the newer bytes are
    /// always the ones wanted — the ledger only ever gains lines. `.atomic` so a share sheet opened
    /// while the write is in flight cannot read half a PDF.
    func write(fileName: String, bytes: Data) -> URL? {
        guard let caches = fileManager.urls(for: .cachesDirectory, in: .userDomainMask).first else { return nil }
        let directory = caches.appendingPathComponent(Self.directory, isDirectory: true)

        do {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            let file = directory.appendingPathComponent(fileName)
            try bytes.write(to: file, options: .atomic)
            return file
        } catch {
            return nil
        }
    }

    /// The same directory name `AndroidStatementExporter` writes into.
    private static let directory = "statements"
}

/// `UIActivityViewController`, as a SwiftUI presentation.
///
/// The statement is shared rather than saved for the reason in ``StatementExporter``'s own note: it is
/// a document the driver decides the destination of, every time. `excludedActivityTypes` is left
/// unset — a driver who wants to mail their statement to an accountant, save it to Files or send it
/// over WhatsApp is doing the thing US-9A.19 is for.
struct ActivityView: UIViewControllerRepresentable {

    let file: URL

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: [file], applicationActivities: nil)
    }

    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {
        // Nothing to push: the controller is built from one immutable item and re-configuring it
        // mid-presentation is how a share in progress gets torn down.
    }
}

/// A file the share sheet is up for.
///
/// `Identifiable` so the sheet is presented with `.sheet(item:)` rather than a `Bool` plus a
/// separately-held `URL` — the pair can disagree for one frame, and the frame it disagrees on is the
/// one where the sheet is up with the previous statement in it.
struct StatementFile: Identifiable {

    let url: URL

    var id: String { url.path }
}
