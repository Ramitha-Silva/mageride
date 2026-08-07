import Foundation
import MageRideShared

/// The app's one open `mageride_passenger.db`, opened on first use and shared.
///
/// **Why a holder rather than a property on ``PassengerGraph``.** Opening the database is `suspend`
/// on the Kotlin side — the file is under `NSFileProtectionCompleteUntilFirstUserAuthentication` and
/// `MageRideDatabaseFactory.openPassenger` is an `async` call — so it cannot be a stored property
/// built in an initialiser without putting a round trip in front of the first frame. `localDbModule`
/// binds two things and deliberately **not** the database for exactly this reason; the app owns the
/// handle.
///
/// **One handle, many callers.** `mobile_db_schema.md` §2 gives this app six tables and eight screen
/// groups will read them — C096's `place_recents`, C099's package projections, C101's addresses.
/// Eight groups calling `openPassenger()` independently would be eight SQLite connections to one
/// encrypted file, which is the defect C075 fixed on the driver side and C093 turned into
/// `DriverDatabase`. Do not call `openPassenger()` anywhere else.
///
/// **An `actor`, not a `@MainActor` class.** Two things follow. The `await` inside ``get()`` is a
/// suspension point, so two callers arriving together would both find `database == nil` and both
/// open one — an actor serialises them, which is the same job the `Mutex` does in
/// `apps/passenger-android/.../di/PassengerDatabase.kt`. And every call on the returned handle is
/// **blocking** (SQLDelight's Native driver is synchronous, as `:shared`'s own docs say twice), so
/// the work belongs off the main thread; an actor is where a caller naturally puts it.
///
/// A failed open answers `nil` rather than throwing. There is nothing a passenger can do about a
/// protected file that will not open, and every caller degrades to "no local history" — the recents
/// list is empty, the map still works, and a ride can still be booked.
actor PassengerDatabase {

    private let factory: MageRideDatabaseFactory
    private var database: PassengerDb?

    init(factory: MageRideDatabaseFactory) {
        self.factory = factory
    }

    /// The open database, opening it on the first call.
    ///
    /// Idempotent and race-safe: a second caller during the first open waits on the actor rather
    /// than starting a second `openPassenger`.
    func get() async -> PassengerDb? {
        if let database { return database }
        let opened = try? await factory.openPassenger(inMemory: false)
        database = opened
        return opened
    }
}
