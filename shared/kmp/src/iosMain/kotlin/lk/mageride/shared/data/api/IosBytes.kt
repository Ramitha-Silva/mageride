package lk.mageride.shared.data.api

import lk.mageride.shared.mqtt.toNSData
import platform.Foundation.NSData

/**
 * A `ByteArray` an API call answered, as the `NSData` Swift already knows how to write and share
 * (C091).
 *
 * **The conversion is here because it cannot be written efficiently in Swift** — the mirror of
 * [capturedDocument], which crosses the same boundary in the other direction and says the same
 * thing. Kotlin/Native exports `ByteArray` as `KotlinByteArray`, a class whose only Swift-facing
 * reader is `get(index:)`: one Objective-C message per byte. `DeviceBinding` copies a 32-byte
 * challenge that way once per install and it is fine; US-9A.19's PDF statement is hundreds of
 * kilobytes and it is not. Kotlin has `memcpy`, so the copy costs a single `NSData.create`.
 *
 * The one caller is `WalletApi.downloadWalletStatementPdf`, whose return type the contract fixes as
 * binary; the CSV arm needs nothing because a Kotlin `String` bridges as an `NSString`.
 *
 * @param bytes What the call returned.
 */
public fun nsDataOf(bytes: ByteArray): NSData = bytes.toNSData()
