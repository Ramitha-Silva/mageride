package lk.mageride.shared

/**
 * Identity of the shared module.
 *
 * `NAME` is the Swift module name produced by `:shared:assembleXCFramework`
 * (`import MageRideShared`) and the Kotlin package root for every app; both are part of the
 * contract with C067/C076 (Android) and C085/C094 (iOS).
 */
public object MageRideShared {
    public const val NAME: String = "MageRideShared"

    /** Root package for everything this module publishes. */
    public const val PACKAGE: String = "lk.mageride.shared"
}
