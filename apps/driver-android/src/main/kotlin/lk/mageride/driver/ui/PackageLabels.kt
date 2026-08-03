package lk.mageride.driver.ui

import androidx.annotation.StringRes
import lk.mageride.driver.R
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.ride.RidePaymentMethod

/**
 * The two labels a package job is read by, on the two screens that show one.
 *
 * SCR-DA-014's offer badges it before the driver accepts (`Package · M`, `Cash on delivery`) and
 * SCR-DA-016a's review sheet repeats both after they have. One table rather than two, because a
 * delivery whose size or payment reads differently on the two screens is a delivery the driver has
 * to check twice.
 *
 * `S`/`M`/`L` and `cod` are **codes**; their names are copy, and copy lives in the three
 * `strings.xml` files.
 */
internal object PackageLabels {

    /** The trilingual name of a package's size (US-20.3). */
    @StringRes
    fun size(size: PackageSize): Int = when (size) {
        PackageSize.S -> R.string.package_size_small
        PackageSize.M -> R.string.package_size_medium
        PackageSize.L -> R.string.package_size_large
    }

    /** How the sender chose to pay, as the driver reads it (AL-22). */
    @StringRes
    fun payment(method: RidePaymentMethod?): Int = when (method) {
        RidePaymentMethod.LANKAQR -> R.string.payment_lankaqr
        RidePaymentMethod.ONEPAY -> R.string.payment_onepay
        RidePaymentMethod.COD -> R.string.payment_cod
        else -> R.string.payment_cash
    }
}
