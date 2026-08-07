import SwiftUI

/// How many GPS samples this handset is holding for the live vehicle (R-17, US-15.1).
///
/// `gps_buffer` is written by ``PositionService`` through `IosPositionPipeline` whether or not
/// anything is on screen, and this is the read side: **the last-known state SCR-DI-035 shows instead
/// of an error**. Per vehicle, because the table is — `seq` is scoped to `(vehicle_id, seq)` and a
/// global count would mix a backlog left behind under a vehicle the driver changed away from.
///
/// **Not ``PositionService/bufferedCount``**, and the difference is a restart. That property is the
/// live pipeline's own count and is zero whenever the service is not running — which is exactly the
/// case this card exists for, since a driver whose app was killed in a tunnel comes back to a
/// backlog and no pipeline. This reads the table, so the answer survives the process. The same shape
/// as `apps/driver-android/.../shell/BufferedSampleCounter.kt`.
///
/// Zero is the answer when there is no live vehicle: a driver who has not chosen one is not
/// publishing, so there is nothing buffered under any id this screen could name.
actor BufferedSampleCounter {

    private let databases: DriverDatabase
    private let vehicles: ActiveVehicleStore

    init(databases: DriverDatabase, vehicles: ActiveVehicleStore) {
        self.databases = databases
        self.vehicles = vehicles
    }

    /// The backlog for the live vehicle. Blocking underneath, which is why this is an actor.
    func buffered() async -> Int64 {
        guard let vehicleId = vehicles.activeVehicleId, let database = await databases.get() else { return 0 }
        return database.gpsBuffer(vehicleId: vehicleId).size()
    }
}

/// **SCR-DI-035's buffered-samples card** — *"128 queued · Replays on reconnect via pos/replay"*.
///
/// The wireframe draws it inside the home sheet under the offline banner: the map greys out, the
/// banner says the connection is gone and this says what the app is doing about it. That is the
/// whole of *"offline mode shows cached state rather than errors"* — the samples are on disk, the
/// `seq` counter has not rewound, and `veh/{vehicleId}/pos/replay` drains them the moment the socket
/// is back (R-17, QoS 1, monotonic `seq`).
///
/// **Self-contained on purpose.** It reads connectivity and the backlog itself rather than taking
/// them as parameters, so the two home sheets — SCR-DI-010's and SCR-DI-011's — each add one line
/// instead of threading two new fields through C088's state. It draws **nothing at all** while the
/// handset is online or the backlog is empty, which is every ordinary moment.
@MainActor
struct BufferedSamplesCard: View {

    @EnvironmentObject private var graph: DriverGraph
    @EnvironmentObject private var connectivity: ConnectivityMonitor

    @State private var queued: Int64 = 0

    var body: some View {
        Group {
            if !connectivity.isOnline, queued > 0 {
                NoticeCard(
                    titleKey: "offline_buffered_title",
                    symbolName: "antenna.radiowaves.left.and.right.slash",
                    accent: MageRideColor.warning
                ) {
                    HStack(spacing: MageRideSpacing.xs) {
                        Text(key: "offline_buffered_replay")
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                            .frame(maxWidth: .infinity, alignment: .leading)

                        StatusPill(label: "offline_buffered_count".localisedFormat(queued), tone: .pending)
                    }
                }
                .accessibilityElement(children: .combine)
            }
        }
        // Polled rather than observed: `gps_buffer` is written from the location callback in the
        // same process, and a SQLDelight query listener would fire on every fix — four redraws a
        // minute for a number that only has to be roughly right.
        .task(id: connectivity.isOnline) {
            guard !connectivity.isOnline else {
                queued = 0
                return
            }
            while !Task.isCancelled, !connectivity.isOnline {
                queued = await graph.bufferedSamples.buffered()
                try? await Task.sleep(nanoseconds: Self.pollNanoseconds)
            }
        }
    }

    /// Five seconds.
    ///
    /// The backlog grows at the D5' §5.2 cadence — one row every one to eight seconds — so a faster
    /// poll would redraw the same number, and a slower one would make a driver watching the count
    /// wonder whether anything was being kept at all.
    private static let pollNanoseconds: UInt64 = 5_000_000_000
}
