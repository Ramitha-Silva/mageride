import Foundation
import MageRideShared

/// The app's one open `mageride_driver.db`, opened on first use and shared (Δ C093).
///
/// **Why a holder rather than a property on ``DriverGraph``.** Opening the database is `suspend` on
/// the Kotlin side — the file is under `NSFileProtectionCompleteUntilFirstUserAuthentication` and
/// `MageRideDatabaseFactory.openDriver` is an `async` call — so it cannot be a stored property built
/// in an initialiser without putting a round trip in front of the first frame. `localDbModule` binds
/// two things and deliberately **not** the database for exactly this reason; the app owns the handle.
///
/// **One handle, three callers.** ``PositionService`` writes `gps_buffer` from the location
/// callback, ``LocalNotificationInbox`` reads and writes `notifications` from the push delegate and
/// SCR-DI-034, and ``BufferedSampleCounter`` reads the backlog for SCR-DI-035's card. Three
/// connections to one protected SQLite file is three write locks contending on one shift; the
/// `PositionService` comment C088 left ("C093's alert inbox and C088's buffered count share it
/// rather than opening a second connection") is this class.
///
/// **An `actor`, not a `@MainActor` class.** Two things follow. The `await` inside ``get()`` is a
/// suspension point, so two callers arriving together would both find `database == nil` and both
/// open one — an actor serialises them, which is the same job the `Mutex` does in
/// `apps/driver-android/.../di/DriverDatabase.kt`. And every call on the returned handle is
/// **blocking** (SQLDelight's Native driver is synchronous, as `:shared`'s own docs say twice), so
/// the work belongs off the main thread; an actor is where a caller naturally puts it.
///
/// A failed open answers `nil` rather than throwing. There is nothing a driver can do about a
/// protected file that will not open, every caller here degrades to "no local history" and the one
/// that cannot — the position buffer — already treats it as a reason not to publish.
actor DriverDatabase {

    private let factory: MageRideDatabaseFactory
    private var database: DriverDb?

    init(factory: MageRideDatabaseFactory) {
        self.factory = factory
    }

    /// The open database, opening it on the first call.
    ///
    /// Idempotent and racy-safe: a second caller during the first open waits on the actor rather
    /// than starting a second `openDriver`.
    func get() async -> DriverDb? {
        if let database { return database }
        let opened = try? await factory.openDriver(inMemory: false)
        database = opened
        return opened
    }
}
