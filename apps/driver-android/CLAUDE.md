# Driver Android Conventions
- Kotlin, Jetpack Compose, Material 3
- Depends on shared/kmp — import DTOs and API client from there
- Screen groups map to D2' wireframes + driver_android.html wireframe
- minSdk 26 — Android 8.0 (URD NFR-22); Gradle project path is `:apps:driver-android`
- Verify: `./gradlew :apps:driver-android:assembleDebug` (needs the Android SDK on the host)

## Walking-skeleton shell (C025) — throwaway, and it claims no screen id

The thinnest driver accept flow: phone OTP sign-in, go on standby, publish position over MQTT, take
an offer with its 15 s countdown, then arrive → start → complete. It owns **no SCR-DA id** —
C068–C070 own the real screens and Wave 4a replaces every composable.

Two values are **typed into the UI**, and both are gaps rather than shortcuts:
- **the `offerId`.** A driver accepts with `POST /v1/rides/{rideId}/offer/{driverId}/accept`, whose
  body requires it, and **no REST response returns one** — `RideDetail` and `RideStateSnapshot` both
  carry `offerExpiresAt` and not the id. It arrives on the `offer.created` push in the finished
  platform (dispatch outbox → `dispatch.events` → notification-svc C051 as FCM, → fanout-svc C041 as
  a socket event); neither exists. `e2e/walking-skeleton` reads that Kafka topic, which an app
  cannot. Recorded as contract gap (a) in the C025 handoff.
- **the MQTT session JWT.** `POST /v1/auth/mqtt-token` (iam.yaml) is not implemented — C020 left it
  to C026 — so nothing can hand this app a device credential. `:shared`'s `MqttSessionTokenManager`
  is already written against that endpoint and C076 uses it the day it lands.

Deliberately absent: the **foreground service** D6' §3 requires of a real Driver App (a shell that
published only while its Activity was resumed would lose a ride's whole track the moment the screen
locked — C076 owns it), the phase-aware cadence (`AdaptiveRateEngine` is in `:shared`, unused here),
trilingual resources, Koin, navigation and the on-device database.

Load-bearing even so:
- the payload is `:shared`'s `PositionCodec` over `:shared`'s `PositionSample` on the topic
  `:shared`'s `MqttTopics` builds — byte for byte what position-processor-svc expects;
- the MQTT **username is the vehicle id**, because `emqx.conf` refuses the CONNECT unless the
  token's `vehicleId` claim equals it and `acl.conf` writes every device rule against it;
- the countdown renders from ride-svc's `offerExpiresAt`, never a local timer — it is ride-svc's
  clock that decides the accept (§11.11), and a second clock would disagree at the boundary;
- `seq` must never rewind: `position-processor-svc` discards everything published after a rewind and
  the vehicle goes dark while the app believes it is publishing (R-17/T-05).

**No `org.jetbrains.kotlin.android` plugin** (AGP 9 refuses it), and HiveMQ's shaded jars need the
`packaging { resources { excludes … } }` block or the APK cannot merge their `META-INF` entries.
