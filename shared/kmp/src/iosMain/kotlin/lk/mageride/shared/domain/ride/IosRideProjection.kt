package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.ride.RideDetail

/**
 * [RideProjection.of] with its defaulted clock applied (C088).
 *
 * **Why a wrapper at all.** `RideProjection.of(detail, clock)` takes the wall clock as a **defaulted**
 * `() -> Timestamp`, and Kotlin default arguments do not survive the Objective-C export — every
 * parameter becomes required. A Swift caller therefore has to supply a closure returning a
 * `kotlin.time.Instant`, and the only ways to produce one on that side are to reach for a stdlib type
 * whose exported spelling is an implementation detail of the compiler, or to write a second clock in
 * Swift for a class that already has the right one. Both are worse than one function.
 *
 * The same argument `colomboBusinessDate` makes, and the same one behind [rideProjectionCanSend]: the
 * default is not a convenience here, it **is** the spec — the projection's clock is what decides
 * whether a grace window (R-16) is still open, and a second one in the app would answer differently
 * the moment the two drifted.
 *
 * @param detail The aggregate as ride-svc last answered it. SCR-DI-015 re-seats the projection on
 *   every full read, exactly as `apps/driver-android`'s `ActiveRideViewModel.refresh` does.
 */
public fun rideProjectionOf(detail: RideDetail): RideProjection = RideProjection.of(detail)

/**
 * [RideProjection.canSend] at the projection's own clock (C088).
 *
 * The local guard SCR-DI-015 puts in front of every command: a move ADD Appendix B.2's table refuses
 * is not sent, which saves the driver a round trip they would spend fifteen seconds of a ride on. It
 * is **never** a claim about what ride-svc would allow — ride-svc is the sole writer (R-01) and its
 * answer is applied whatever the table says.
 *
 * @param projection The screen's projection.
 * @param command The command the driver's tap maps to.
 */
public fun rideProjectionCanSend(projection: RideProjection, command: RideCommand): Boolean =
    projection.canSend(command)
