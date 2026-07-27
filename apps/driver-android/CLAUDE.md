# Driver Android Conventions
- Kotlin, Jetpack Compose, Material 3
- Depends on shared/kmp — import DTOs and API client from there
- Screen groups map to D2' wireframes + driver_android.html wireframe
- minSdk 26 — Android 8.0 (URD NFR-22); Gradle project path is `:apps:driver-android`
- Verify: `./gradlew :apps:driver-android:assembleDebug` (needs the Android SDK on the host)
