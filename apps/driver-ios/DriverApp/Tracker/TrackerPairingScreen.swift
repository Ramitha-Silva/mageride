import MageRideShared
import SwiftUI

/// **SCR-DI-027 · GPS tracker pairing** (US-3.1/3.2, US-3.21–3.23, T-02/T-09).
///
/// The wireframe, top to bottom: a `‹ Back` / *"Pair GPS tracker"* bar, one grouped list holding
/// **Vehicle** and **IMEI**, a two-button row (`▣ Scan device QR` · `Bind code`), the `Pair device`
/// CTA, the green *"✦ Hardware tracker behaviour"* card, and the fleet-CSV note.
///
/// **Two of the wireframe's controls do less than it draws, and both are the wrapper's doing.**
/// `POST /v1/vehicles/{vehicleId}/device` — the only tracker route this app has — takes `{ imei }` and
/// nothing else, while provisioning-svc's own bind takes `method: [manual, qr, admin_code]` and a
/// `bindCode`. So **Bind code** has nothing to send and is drawn disabled with the reason under it,
/// and a scanned IMEI reaches the server indistinguishable from a typed one. Both are C074 spec gaps
/// carried forward; see ``TrackerRepository``.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct TrackerPairingScreen: View {

    @StateObject private var model: TrackerPairingModel

    init(model: @autoclosure @escaping () -> TrackerPairingModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                banner

                GroupedList {
                    vehicleRow
                    imeiRow
                }

                if let binding = model.state.binding {
                    pairedCard(binding)
                }

                entryMethods

                Button {
                    Task { await model.pair() }
                } label: {
                    Text(key: "tracker_pair_action")
                }
                .buttonStyle(.mageCta(loading: model.state.isPairing))
                .disabled(!model.state.canPair)

                hardwareBehaviour
                fleetCsv
            }
            .padding(MageRideSpacing.md)
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "tracker_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.refresh() }
        .refreshable { await model.refresh() }
        .sheet(
            isPresented: Binding(
                get: { model.state.isScanning },
                set: { if !$0 { model.cancelScan() } }
            )
        ) {
            DeviceQrScannerSheet(onScanned: model.onScanned, onDismiss: model.cancelScan)
        }
    }

    // MARK: - The strip

    /// US-3.4 / T-08. A duplicate IMEI is not a failure to retry — the serial is already active
    /// somewhere and provisioning-svc has quarantined it — so it gets its own copy rather than the
    /// generic *"try again"*. Everything else lands as the resolved failure copy (D-26).
    @ViewBuilder
    private var banner: some View {
        if let errorKey = model.state.errorKey {
            DashboardBanner(
                text: errorKey.localised,
                accent: MageRideColor.error,
                symbolName: model.state.isQuarantined ? "exclamationmark.shield.fill" : nil
            )
            .onTapGesture(perform: model.dismissError)
        }
    }

    // MARK: - The two rows

    /// The wireframe's `Vehicle   ABC-1234 ›`.
    ///
    /// Every vehicle the driver owns or has been assigned is offered, Mode A/B included: a tracker on a
    /// fleet bus is precisely the US-3.22 case, and the app's Mode-C-only fence (AL-27) is about
    /// *onboarding* a vehicle, not about pairing hardware to one somebody else onboarded.
    ///
    /// A menu `Picker` rather than a segmented one: a driver may hold several vehicles and the row has
    /// the width of a value, not of a control per option.
    private var vehicleRow: some View {
        GroupedRow(
            titleKey: "tracker_vehicle_label",
            subtitleKey: model.state.hasNoVehicle ? "tracker_no_vehicles" : nil,
            showsSeparator: true
        ) {
            Picker(
                selection: Binding(
                    get: { model.state.selectedVehicleId ?? "" },
                    set: { model.select(vehicleId: $0) }
                )
            ) {
                ForEach(model.state.vehicles, id: \.vehicleId) { vehicle in
                    Text(vehicleLabel(vehicle)).tag(vehicle.vehicleId)
                }
            } label: {
                Text(key: "tracker_vehicle_label")
            }
            .pickerStyle(.menu)
            .labelsHidden()
            .disabled(model.state.vehicles.isEmpty)
        }
    }

    /// The wireframe's `IMEI   8612 3456 …`, as the field D2' §SCR-DI-027's SwiftUI column names.
    private var imeiRow: some View {
        GroupedRow(
            titleKey: "tracker_imei_label",
            subtitleKey: model.state.isImeiRejected ? "tracker_imei_invalid" : "tracker_imei_help",
            showsSeparator: false
        ) {
            TextField(
                "",
                text: Binding(get: { model.state.imei }, set: model.onImeiChange)
            )
            .mageFont(.body)
            .foregroundStyle(model.state.isImeiRejected ? MageRideColor.error : MageRideColor.onSurface)
            .keyboardType(.numberPad)
            .multilineTextAlignment(.trailing)
            .disabled(model.state.isPaired)
            .accessibilityLabel(Text(key: "tracker_imei_label"))
        }
    }

    // MARK: - The button row

    /// The wireframe's `[▣ Scan device QR] [Bind code]`.
    ///
    /// **Bind code** is disabled and the line underneath says why: the owner-facing wrapper carries no
    /// `bindCode`, so there is nowhere for one to be sent. Scan is disabled on a handset whose scanner
    /// cannot run — every simulator, and any device older than the A12 `DataScannerViewController`
    /// needs — rather than hidden, for the same reason.
    private var entryMethods: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            HStack(spacing: MageRideSpacing.xs) {
                OutlinedAction(
                    labelKey: "tracker_scan_action",
                    symbolName: "qrcode.viewfinder",
                    isEnabled: !model.state.isPaired && model.isScanSupported
                ) {
                    Task { await model.startScan() }
                }

                OutlinedAction(labelKey: "tracker_bind_code_action", isEnabled: false) {}
            }

            Text(key: "tracker_bind_code_help")
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
    }

    // MARK: - The cards

    /// D2' §SCR-DI-027's *"paired → cert-issued confirmation"*.
    ///
    /// The `201` from the bind **is** the credential: provisioning-svc mints the X.509 or the signed
    /// PSK inside that call (T-02, D6' §4.2), so a `bindingId` in hand means one was issued. What this
    /// card cannot show is the device's live health — `lastSeen`, `battery`, `signal` come from
    /// `GET /v1/trackers/{imei}`, which has no client here (C074 spec gap 3).
    private func pairedCard(_ binding: TrackerBinding) -> some View {
        NoticeCard(
            titleKey: "tracker_paired_title",
            symbolName: "antenna.radiowaves.left.and.right",
            accent: MageRideColor.success
        ) {
            Text("tracker_paired_body".localisedFormat(TrackerImei.grouped(binding.imei)))
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
    }

    /// The wireframe's green *"✦ Hardware tracker behaviour"* card — and the C092 fence, in words.
    ///
    /// US-3.6: once the device is bound it is the single active publisher and the phone stops.
    /// US-3.22/3.23: a Mode A/B tracker starts and ends journeys on ignition with no app involved, so
    /// the dashboard is *already* at *"Journey started"* when the driver opens it. US-3.21: a Mode C
    /// tracker's GPS is accepted only while the driver is Online.
    private var hardwareBehaviour: some View {
        NoticeCard(
            titleKey: "tracker_behaviour_title",
            symbolName: "sparkles",
            accent: MageRideColor.success
        ) {
            VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
                ForEach(Self.behaviourKeys, id: \.self) { key in
                    Text(key: key)
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
    }

    /// The wireframe's *"Fleet (5,000+)? Use Admin Portal CSV ›"* (US-3.2, T-09).
    ///
    /// Drawn as a note rather than as a link: the bulk path is `POST /v1/fleets/{fleetId}/trackers/bulk`
    /// on provisioning-svc and it is the **Admin Portal's**, and this app has no Admin Portal origin to
    /// open — ``DriverEnvironment`` carries the gateway and the MQTT host, and D7' puts the portal on a
    /// different one. A `›` that opened nothing would be worse than a sentence that says where to go.
    private var fleetCsv: some View {
        NoticeCard(symbolName: "square.and.arrow.up.on.square", accent: MageRideColor.secondary) {
            Text(key: "tracker_fleet_csv")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
    }

    /// *"Three-wheeler · ABC-1234"* — the type is trilingual and the plate is not.
    private func vehicleLabel(_ vehicle: VehicleSummary) -> String {
        vehicle.vehicleType.labelKey.localised + MageRideSymbols.separator + vehicle.registrationNumber
    }

    private static let behaviourKeys = [
        "tracker_behaviour_publisher",
        "tracker_behaviour_ignition",
        "tracker_behaviour_mode_c",
    ]
}
