import MageRideShared

/// SCR-PI-006's answer — which of what is on the map (US-7.7).
///
/// **A view preference and nothing more.** Three things decide whether a vehicle reaches this filter
/// at all, and none of them is here: a Mode C vehicle on active hire is dropped by fanout-svc from
/// the public geocell groups (US-7.16), a vehicle past the freshness window is dropped for staleness
/// (US-7.17), and a Mode B vehicle appears only for a passenger the `share:{userId}` entitlement
/// covers (D-23). Turning *Mode B* on cannot conjure a vehicle the passenger has no grant for, and
/// turning *Mode C* on cannot resurrect one that is on a fare. `MapFilterTests` pins that.
///
/// **A value, not a service, and it is applied on the device.** The wireframe's own state line reads
/// *"instant filter"*, so it is re-applied to *every* batch rather than to the first one — a screen
/// that filtered once and appended would show a type the passenger had switched off the moment that
/// vehicle next moved. See ``LiveMapState/lastFrames``.
///
/// Pure Swift: it holds ``ModeToken`` and ``VehicleToken``, not the wire enums, so it is `Hashable`,
/// `Equatable` and testable without the framework. The bridge from a frame is ``allows(_:)``.
struct MapFilter: Equatable {

    /// Which of A / B / C are shown. All three by default.
    var modes: Set<ModeToken> = Set(VehicleLabels.modeRows)

    /// Which of ``VehicleLabels/chipTypes`` are shown. All eight by default.
    var types: Set<VehicleToken> = Set(VehicleLabels.chipTypes)

    /// Whether [frame] belongs on the map.
    ///
    /// A vehicle whose type has **no chip** is never hidden by the type row — see
    /// ``VehicleLabels/chipTypes``. It is *unfilterable rather than hidden*: a marker that vanished
    /// with no control to bring it back is a bug a passenger cannot diagnose. The mode toggle still
    /// applies to it, because every vehicle has a mode.
    func allows(_ frame: VehicleFrame) -> Bool {
        let modeAllowed = ModeToken.forMode(frame.mode).map(modes.contains) ?? true
        guard let type = VehicleToken.forType(frame.type), VehicleLabels.chipTypes.contains(type) else {
            return modeAllowed
        }
        return modeAllowed && types.contains(type)
    }

    /// The wireframe's mode rows.
    func withMode(_ mode: ModeToken, enabled: Bool) -> MapFilter {
        var next = self
        if enabled { next.modes.insert(mode) } else { next.modes.remove(mode) }
        return next
    }

    /// The wireframe's type chips.
    func withType(_ type: VehicleToken, enabled: Bool) -> MapFilter {
        var next = self
        if enabled { next.types.insert(type) } else { next.types.remove(type) }
        return next
    }

    /// Whether anything at all is shown — an all-off filter is one of the three ways the map can be
    /// empty, and the only one the passenger can undo (US-7.14).
    var showsNothing: Bool { modes.isEmpty || types.isEmpty }
}
