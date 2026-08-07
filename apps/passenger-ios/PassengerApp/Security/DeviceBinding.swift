import DeviceCheck
import Foundation
import MageRideShared

/// D-30 and ADD §12.1 on this platform, in one place: **App Attest** for the `X-Attestation` header
/// and the **Keychain + Secure Enclave** for everything that has to survive a relaunch.
///
/// Almost nothing here is an implementation, and that is the point — both halves already exist in
/// `:shared` and are bound by `iosAppModule`:
///
/// - **The Keychain half** is C014's `PlatformSecureStore`: `kSecClassGenericPassword` items whose
///   data-protection class key is wrapped by the Secure Enclave's UID key, so the ciphertext on disk
///   is bound to the hardware. Two attributes carry the whole argument —
///   `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (out of iCloud Keychain and out of every
///   backup, so a restored backup cannot resume a session on another handset), and `kSecAttrService`,
///   namespaced per surface so the driver and passenger apps cannot see each other's session (AL-08).
///   The service name is ``PassengerEnvironment/keychainService``; the **absence** of a keychain
///   access group in `PassengerApp.entitlements` is the other half of that fence.
/// - **The device id** is `AuthSessionStore`'s: a ULID minted on first use and kept in that same
///   Keychain and bound into the session at OTP verify. It is deliberately not
///   `identifierForVendor`, which resets when the last app from a vendor is deleted — a passenger
///   who reinstalled would look like a new device and AL-08 would revoke the session they still had.
/// - **App Attest** is C014's `PlatformAttestationProvider`: the key is generated *in* the Secure
///   Enclave by `DCAppAttestService` and never leaves it, only its id is stored, and the header is
///   `base64url(keyId) "." base64url(assertion)` signed over `SHA-256("<METHOD> <path>")` — the
///   format `backend/src/ApiGateway/Attestation` parses and no spec states.
///
/// What this type adds is the two questions a *screen* has to ask: whether this device can attest at
/// all, and what to do about the registration that has no endpoint. It is
/// `apps/driver-ios/DriverApp/Security/DeviceBinding.swift` with the MQTT half removed — this app
/// has no second credential, because it has no broker (D3' §3.3).
enum DeviceBinding {

    /// Whether App Attest works on this device.
    ///
    /// `false` on **every simulator** — Apple ships no App Attest there at all — which is why the
    /// simulator run this component's DoD names cannot prove the header end to end. It is also false
    /// on a jailbroken device and on hardware without a Secure Enclave.
    static var canAttest: Bool { DCAppAttestService.shared.isSupported }

    /// Generates the attestation object for this install, against a server-issued `challenge`.
    ///
    /// **There is nowhere to send it, and that is a recorded contract gap** (C014's handoff, gap
    /// (b)). `backend/contracts/iam.yaml` has no App Attest registration route and no challenge
    /// endpoint, and the gateway's `IAttestedKeyStore` is fed from `iam.devices
    /// .attestation_verified_at` — a column nothing writes. Until
    /// `POST /v1/auth/attestation/challenge` and `POST /v1/auth/attestation/register` exist, this
    /// build can produce assertions the edge cannot check, so D-30's twenty-three attested
    /// operations answer `401 attestation-failed` on iOS while the identical Android build passes.
    /// This is the second component for which it is a shipped-behaviour gap rather than a future
    /// one; C085 recorded it as gap (c) and it is unchanged.
    ///
    /// The method is here rather than deleted because the *client* half is complete and the gap is
    /// one endpoint wide: when the route lands, the login screen calls this and posts the result. Deleting it
    /// would hide a finished half behind an unfinished one. C095 is the caller when the route lands.
    static func prepareRegistration(
        challenge: Data,
        provider: PlatformAttestationProvider
    ) async -> AppAttestRegistration? {
        guard canAttest else { return nil }
        return try? await provider.prepareRegistration(challenge: KotlinByteArray.from(challenge))
    }
}

extension KotlinByteArray {

    /// A `Data` as the `ByteArray` the Kotlin side takes.
    ///
    /// Kotlin's `ByteArray` reaches Swift as `KotlinByteArray`, which has no initialiser from
    /// `Data` — the bytes are copied one at a time because that is the only API the exported class
    /// offers. A challenge is 32 bytes, once per install.
    static func from(_ data: Data) -> KotlinByteArray {
        let array = KotlinByteArray(size: Int32(data.count))
        for (index, byte) in data.enumerated() {
            array.set(index: Int32(index), value: Int8(bitPattern: byte))
        }
        return array
    }
}
