import Foundation
import MageRideShared

/// SCR-PI-017's state.
struct PayFareState {

    /// The rail SCR-PI-016 confirmed, carried here by ``PaymentSelection``.
    var method: PaymentMethod = PaymentMethod.scanDriverQr

    var amountMinor: Int64?

    var paymentId: String?
    var paymentState: PaymentState?

    /// The **driver's own** bank QR, as a signed URL (AL-59). Present only on the driver-QR rail, and
    /// only when their payout profile carries an image — it is a fallback for a handset whose camera
    /// cannot read the printed one, never a MageRide code (AL-22).
    var qrImageUrl: String?

    var isScanning = false
    var isBusy = false

    /// AL-47's *"I've paid"* has been sent; the screen is waiting for the driver.
    var isClaimed = false

    /// How long since the claim. Past five minutes the screen offers support.
    var secondsWaiting = 0

    /// The camera is refused or restricted — Settings is the only way past it, and the bank-app link
    /// is the way around it.
    var isCameraBlocked = false

    var errorKey: String?

    /// Whether the screen can show `Confirmed ✓`.
    ///
    /// `isTerminal` rather than a hand-written list: `Disputed` and `Refunded` are terminal too and
    /// are equally *"nothing more for this screen to do"*. What must **not** be in here is
    /// `QrClaimedByPassenger` — that is the wait, and treating it as settled would tell a passenger
    /// their driver had confirmed when the driver has not been asked yet.
    var isConfirmed: Bool { paymentState?.isTerminal == true }

    /// Whether *"Get help"* should be offered — the claim has gone unanswered past the nudge.
    var offersSupport: Bool {
        isClaimed && !isConfirmed && secondsWaiting >= PayFareState.unconfirmedSeconds
    }

    /// Whether the scan panel and its two buttons are what the screen is showing.
    var isDriverQr: Bool { method == PaymentMethod.scanDriverQr }

    /// `88s`, as the wireframe writes it.
    var waiting: String { "pay_waiting_seconds".localisedFormat(secondsWaiting) }

    /// AL-47 re-pushes the driver at +5 min; past that the passenger is offered support.
    static let unconfirmedSeconds = 300
}

/// SCR-PI-017 — paying, and the attestation that follows.
///
/// **The app renders no MageRide QR.** AL-22: the passenger *scans the driver's* printed or on-screen
/// LankaQR, or opens their own bank app through a LankaQR link (AL-15). Both move money bank to bank,
/// which is exactly why **no callback ever reaches fare-svc** and why settlement is AL-47's
/// attestation: *"I've paid"* → the driver confirms → `DriverConfirmedQR`, terminal.
///
/// **A claim without a confirm is not a failure, it is a wait.** The driver is re-pushed at five
/// minutes; past that the screen offers Support, which routes to the Finance dispute queue. No money
/// moves either way — there is nothing for the platform to reverse.
///
/// **A wallet fare is `Succeeded` the moment the initiation returns** — one balanced `trip_payment`
/// entry, passenger wallet to driver wallet, no gateway and no `Pending` (AL-57) — so the screen goes
/// straight to the receipt. A cash fare settles on the driver's confirmation and shows *"Settling…"*
/// until it does.
@MainActor
final class PayFareModel: ObservableObject {

    @Published private(set) var state = PayFareState()

    private let rideId: String
    private let rides: RideRepository
    private let camera: CameraAuthoriser
    private let bank: BankAppHandoff
    private let now: () -> Date
    private let pollInterval: TimeInterval

    private var claimedAt: Date?
    private var work: [Task<Void, Never>] = []
    private var hasStarted = false

    init(
        rideId: String,
        method: PaymentMethod,
        rides: RideRepository,
        camera: CameraAuthoriser,
        bank: BankAppHandoff,
        now: @escaping () -> Date = Date.init,
        pollInterval: TimeInterval = 5
    ) {
        self.rideId = rideId
        self.rides = rides
        self.camera = camera
        self.bank = bank
        self.now = now
        self.pollInterval = pollInterval
        state.method = method
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Reads the fare and starts D-10 on the chosen rail. Idempotent.
    func start() {
        guard !hasStarted else { return }
        hasStarted = true
        work.append(Task { await self.load() })
    }

    /// *"📷 Scan driver's QR"*.
    ///
    /// **The grant is asked for before the scanner is presented**, because
    /// `DataScannerViewController.isAvailable` is `false` without it and presenting first would show
    /// a viewfinder that cannot see. A refusal is not an error state — AL-15's bank-app link and
    /// AL-47's claim both still work, which is what the screen says instead.
    func openScanner() {
        state.errorKey = nil
        work.append(Task {
            switch camera.access {
            case .granted:
                break
            case .notDetermined:
                guard await camera.request() else {
                    state.isCameraBlocked = true
                    return
                }
            case .blocked:
                state.isCameraBlocked = true
                return
            }

            guard camera.isScannerSupported else {
                // Every simulator, and any handset older than the A12 the data scanner needs. Not a
                // permission problem, so it does not send anybody to Settings.
                state.isCameraBlocked = true
                return
            }
            state.isCameraBlocked = false
            state.isScanning = true
        })
    }

    func closeScanner() {
        state.isScanning = false
    }

    /// Opens this app's page in Settings, for a passenger who refused the camera and changed their
    /// mind.
    func openCameraSettings() {
        camera.openSettings()
    }

    /// AL-15's link — the passenger's own bank app, opened against the driver's QR.
    ///
    /// **A handset with no bank app falls back to the camera**, which is the same decision that made
    /// the scan the primary control and this the second one: what must not happen is a tap that
    /// appears to do nothing.
    func openBankApp() {
        work.append(Task {
            guard await bank.openBankApp() == false else { return }
            openScanner()
        })
    }

    /// The camera decoded the driver's QR.
    ///
    /// The payload goes to `POST /v1/fare/pay/scan-driver-qr` **as read** — it is the driver's
    /// merchant string and this app does not interpret it. Recording the scan is what lets a later
    /// dispute say which QR was presented.
    func onQrScanned(_ payload: String) {
        state.isScanning = false
        state.isBusy = true
        state.errorKey = nil

        work.append(Task {
            do {
                let status = try await rides.payByScanningDriverQr(rideId: rideId, qrPayload: payload)
                guard !Task.isCancelled else { return }
                state.isBusy = false
                state.paymentId = status.paymentId
                state.paymentState = status.state
            } catch is CancellationError {
                return
            } catch {
                state.isBusy = false
                state.errorKey = RideErrors.messageKey(for: error)
            }
        })
    }

    /// AL-47's *"I've paid"*.
    ///
    /// Offered whether or not the scan went through this app, because the passenger may well have
    /// paid from their bank app instead — the platform saw neither.
    ///
    /// - Parameter receiptArtifactId: The bank app's receipt screenshot, when one was attached.
    ///   **This is what a dispute is adjudicated on** — there is no gateway record to fall back to.
    ///   Nothing on this surface uploads one yet: `rides.proof_artifacts` is written by the driver's
    ///   delivery proof and by no passenger-facing route, so the parameter exists and is `nil`.
    func claimPaid(receiptArtifactId: String? = nil) {
        guard !state.isBusy else { return }
        state.isBusy = true
        state.errorKey = nil

        work.append(Task {
            do {
                let status = try await rides.claimDriverQrPaid(
                    rideId: rideId,
                    receiptArtifactId: receiptArtifactId
                )
                guard !Task.isCancelled else { return }
                claimedAt = now()
                state.isBusy = false
                state.isClaimed = true
                state.paymentId = status.paymentId
                state.paymentState = status.state
                await awaitDriverConfirmation()
            } catch is CancellationError {
                return
            } catch {
                state.isBusy = false
                state.errorKey = RideErrors.messageKey(for: error)
            }
        })
    }

    /// US-8.15 — a rail that will not settle becomes cash, without losing the payment's history.
    func switchToCash() {
        guard let paymentId = state.paymentId, !state.isBusy else { return }
        state.isBusy = true
        state.errorKey = nil

        work.append(Task {
            do {
                let status = try await rides.fallbackToCash(paymentId: paymentId)
                guard !Task.isCancelled else { return }
                state.isBusy = false
                state.paymentState = status.state
            } catch is CancellationError {
                return
            } catch {
                state.isBusy = false
                state.errorKey = RideErrors.messageKey(for: error)
            }
        })
    }

    /// The cell's `Retry` link.
    ///
    /// Two different things, because the screen has two different stalls: **before** a payment exists
    /// it re-runs `POST /v1/fare/pay` (the initiation failed, and the amount on screen came from the
    /// ride read), and **after** one does it re-reads the status rather than posting a second
    /// payment. Retrying an initiation that succeeded would be a second `ride_payments` row for one
    /// fare.
    func retry() {
        state.errorKey = nil
        guard let paymentId = state.paymentId else {
            work.append(Task { await self.initiate(state.method) })
            return
        }
        work.append(Task { await self.readStatus(paymentId) })
    }

    func clearError() {
        state.errorKey = nil
    }

    // MARK: -

    private func load() async {
        do {
            let ride = try await rides.ride(rideId: rideId)
            guard !Task.isCancelled else { return }
            state.amountMinor = ride.amountMinorOrNil
        } catch is CancellationError {
            return
        } catch {
            state.errorKey = RideErrors.messageKey(for: error)
        }
        await initiate(state.method)
    }

    /// `POST /v1/fare/pay`.
    private func initiate(_ method: PaymentMethod) async {
        state.isBusy = true
        state.errorKey = nil
        do {
            let initiation = try await rides.pay(rideId: rideId, method: method)
            guard !Task.isCancelled else { return }
            state.isBusy = false
            state.paymentId = initiation.paymentId
            state.paymentState = initiation.state
            state.qrImageUrl = initiation.driverQr?.qrImageUrl
            state.amountMinor = initiation.amountMinor
        } catch is CancellationError {
            return
        } catch {
            state.isBusy = false
            state.errorKey = RideErrors.messageKey(for: error)
        }
    }

    /// The wait between *"I've paid"* and `Confirmed ✓`.
    ///
    /// A poll rather than a socket subscription: `DriverConfirmedQR` is a **payment** transition and
    /// the passenger's SignalR groups are the ride's, so there is no event to subscribe to. The
    /// counter is what turns a five-minute silence into an offer of help rather than a spinner.
    private func awaitDriverConfirmation() async {
        guard let paymentId = state.paymentId else { return }
        while !Task.isCancelled, !state.isConfirmed {
            state.secondsWaiting = claimedAt.map { Int(now().timeIntervalSince($0)) } ?? 0
            try? await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
            guard !Task.isCancelled else { return }
            await readStatus(paymentId)
        }
    }

    /// One status read. A failure is swallowed: the driver confirming is not something this app can
    /// hurry, and a lost read is one the next poll makes again.
    private func readStatus(_ paymentId: String) async {
        guard let status = try? await rides.paymentStatus(paymentId: paymentId), !Task.isCancelled else {
            return
        }
        state.paymentState = status.state
    }
}
