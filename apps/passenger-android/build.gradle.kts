import javax.inject.Inject

// MageRide — Passenger Android application shell (C076 passenger-android-shell).
//
// The host every passenger screen group plugs into: theme, trilingual resources, navigation and
// its drawer, the MapLibre map, the SignalR live plane, the Koin graph and FCM. It owns NO
// wireframe screen — C077–C084 do. What was here before was C025's walking skeleton; that slice
// declared itself throwaway and this replaces it.
//
// Verify: ./gradlew :apps:passenger-android:testDebugUnitTest :apps:passenger-android:assembleDebug
//
// **This app has no MQTT client and never will.** D3' §3.3 splits the two real-time planes:
// device position INGEST is MQTT, passenger realtime-OUT is SignalR. A passenger publishes no
// position to the broker, so there is no HiveMQ dependency, no foreground service and no
// `MQTT_HOST` build field — the three things this build script would otherwise mirror from
// `apps/driver-android`.

// NO `org.jetbrains.kotlin.android`. AGP 9 has built-in Kotlin support and refuses the plugin
// outright ("no longer required for Kotlin support since AGP 9.0"), the same way it refuses
// `com.android.library` in a KMP project — see shared/kmp/CLAUDE.md.
//
// The `google-services` plugin is deliberately absent even though firebase-messaging is a
// dependency: it hard-fails the build without a `google-services.json`, and the Firebase project
// is C124's (D7' §13 secrets). FCM compiles and registers here; it does not deliver until that
// file lands. Same call C067 made.
plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.detekt)
    alias(libs.plugins.ktlint)
}

// ---------------------------------------------------------------------------------------
// C017's H3 native library, lifted out of the jar and packaged as a real JNI lib.
//
// `com.uber:h3` carries its natives as ORDINARY JAR RESOURCES — `/android-arm64/libh3-java.so`,
// `/linux-x64/libh3-java.so`, `/windows-x64/libh3-java.dll` and ten more — and
// `H3Core.newInstance()` unpacks whichever matches the running ABI to a temp file and
// `System.load`s it. That works on a desktop JVM, which is why every unit test in this module and
// the e2e harness exercise the real grid and pass; it can NEVER work inside an APK:
//
//   * AGP's java-resource merger drops every `*.so` outright — native libraries are
//     `MergeNativeLibsTask`'s job, not a resource's.
//   * `MergeNativeLibsTask` only recognises `lib/<abi>/*.so` inside a jar, which
//     `android-arm64/libh3-java.so` is not.
//
// So the APK shipped 1.5 MB of macOS `.dylib` and Windows `.dll` (neither matches `*.so`) while
// the one file Android actually needs was silently absent, and the first `grid.cellAt()` after a
// passenger granted location died with
// `UnsatisfiedLinkError: No native resource found at /android-arm64/libh3-java.so` on a
// `Dispatchers.Default` worker — a process kill, not a caught failure.
//
// The fix is a jniLibs tree under the ABI names Android uses, loaded with `System.loadLibrary`
// (`H3Core.newSystemInstance()`); see `PlatformH3Grid.jvmShared.kt`, which keeps the unpack path
// as its fallback so the JVM target is unaffected.
//
// h3 4.4.0 ships `android-arm64` and `android-arm` and nothing else — there is no x86 or x86_64
// native, so an EMULATOR still has no H3. That is why `PassengerLiveMap` treats a missing engine
// as a degraded live map rather than something to throw through a coroutine.
// ---------------------------------------------------------------------------------------
val h3NativeLib = configurations.dependencyScope("h3NativeLib")

val h3NativeLibPath = configurations.resolvable("h3NativeLibPath") {
    extendsFrom(h3NativeLib.get())
    // The natives are all this needs; h3's classes reach the app through `:shared`.
    isTransitive = false
}

dependencies {
    add(h3NativeLib.name, libs.h3)
}

/**
 * Copies `<h3-dir>/libh3-java.so` out of the jar and back in under `<abi>/libh3-java.so`.
 *
 * A task type rather than a plain `Sync` because AGP 9 refuses a bare `TaskProvider` on the
 * SourceSet API — a generated source directory has to be wired through the Variant API, and that
 * wants a task with a `DirectoryProperty` output it can place itself.
 */
abstract class ExtractH3Natives : DefaultTask() {

    /** The `com.uber:h3` jar. A file collection so the resolution stays lazy. */
    @get:InputFiles
    @get:PathSensitive(PathSensitivity.NONE)
    abstract val h3Jar: ConfigurableFileCollection

    /** Set by AGP through `addGeneratedSourceDirectory` — do not point this anywhere by hand. */
    @get:OutputDirectory
    abstract val jniLibs: DirectoryProperty

    @get:Inject
    abstract val archives: ArchiveOperations

    @get:Inject
    abstract val files: FileSystemOperations

    @TaskAction
    fun extract() {
        files.sync {
            from(archives.zipTree(h3Jar.singleFile)) {
                include(ABI_DIRS.keys.map { "$it/$LIB_NAME" })
                eachFile { path = "${ABI_DIRS.getValue(path.substringBefore('/'))}/$name" }
            }
            includeEmptyDirs = false
            into(jniLibs)
        }
    }

    private companion object {

        const val LIB_NAME = "libh3-java.so"

        /**
         * h3's own directory names on the left, Android's ABI names on the right.
         *
         * Nothing renames these for us: the jar was built for a desktop unpacker that never had
         * to care what an APK calls an ABI. An entry missing from the jar is simply not copied —
         * h3 4.4.0 has no x86 or x86_64 Android native at all.
         */
        val ABI_DIRS = mapOf(
            "android-arm64" to "arm64-v8a",
            "android-arm" to "armeabi-v7a",
        )
    }
}

val extractH3Natives = tasks.register<ExtractH3Natives>("extractH3Natives") {
    description = "Unpacks libh3-java.so out of com.uber:h3 into a jniLibs tree AGP will package."
    h3Jar.from(h3NativeLibPath)
}

// `lib/<abi>/libh3-java.so` in every variant, which is the only place `System.loadLibrary` looks.
androidComponents {
    onVariants { variant ->
        variant.sources.jniLibs?.addGeneratedSourceDirectory(extractH3Natives, ExtractH3Natives::jniLibs)
    }
}

android {
    namespace = "lk.mageride.passenger"
    compileSdk = libs.versions.androidCompileSdk.get().toInt()

    defaultConfig {
        applicationId = "lk.mageride.passenger"
        // URD NFR-22 — Android 8.0. Not a preference; do not raise without a micro-change-set.
        minSdk = libs.versions.androidMinSdk.get().toInt()
        targetSdk = libs.versions.androidTargetSdk.get().toInt()
        versionCode = 1

        // Reaches the gateway as `X-App-Version`, where D-31's minimum-version gate reads it. The
        // gateway's floor is 1.0.0 (C008), so anything below this is answered 426 on every route.
        // `PassengerEnvironment` reads it back out of BuildConfig — one number, not two.
        versionName = "1.0.0"
    }

    androidResources {
        // Si and Ta are not "extra" locales — CLAUDE.md makes all three mandatory, and En is the
        // fallback rather than the primary. Listing them keeps AGP's resource shrinker and the
        // Play language split from dropping the two that matter most here.
        localeFilters += setOf("en", "si", "ta")
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // ---------------------------------------------------------------------------------
    // Build-type configuration is the deployment surface, and it belongs in one place.
    //
    // Everything below reaches Kotlin through BuildConfig and is read exactly once, by
    // `PassengerEnvironment` (di/PassengerEnvironment.kt). No other file in this module may read
    // BuildConfig: a value that is read in two places is a value that gets overridden in one.
    // ---------------------------------------------------------------------------------
    buildTypes {
        getByName("debug") {
            isMinifyEnabled = false
            applicationIdSuffix = ".debug"

            // The replica edge, NOT the emulator loopback.
            //
            // `http://10.0.2.2:5000` is the host machine's localhost as seen from inside an
            // EMULATOR. On a physical handset it resolves to nothing, so every call times out and
            // the app shows its generic "Something went wrong" — which is what a debug APK on a
            // real phone did, with no request ever reaching the edge to explain it.
            //
            // Pointed at the replica so a debug build can be exercised on a real device. Revert
            // to `http://10.0.2.2:5000` for emulator work against the local dev stack; the
            // release variant below is unaffected either way.
            //
            // The SignalR hub is on the same origin (`/hubs/live`), so there is one value here
            // and not two. `usesCleartextTraffic` stays in the debug manifest for the PMTiles
            // value below, which is still loopback.
            buildConfigField("String", "API_BASE_URL", "\"https://api.mageride.lk\"")
            // PMTiles archive for the map style, on the tile host rather than the emulator
            // loopback — same reason as API_BASE_URL above: `10.0.2.2` is nothing on a physical
            // handset, so the basemap renders blank while the markers still draw. Revert to
            // `http://10.0.2.2:8080/lk.pmtiles` for emulator work against the local dev stack.
            buildConfigField("String", "PMTILES_URL", "\"https://tiles.mageride.lk/lk.pmtiles\"")
            // Play Console cloud project number for Play Integrity (D-30). 0 means "this build
            // cannot attest", which `PlatformAttestationProvider` turns into a null header and
            // the gateway into a 401 — the intended failure mode, never a silent bypass.
            buildConfigField("long", "INTEGRITY_CLOUD_PROJECT", "0L")
        }
        getByName("release") {
            // C103 owns signing and the R8 rules; what is fixed here is the deployment surface,
            // because a release build pointed at the dev gateway is the one mistake that cannot
            // be caught by a test.
            isMinifyEnabled = false
            buildConfigField("String", "API_BASE_URL", "\"https://api.mageride.lk\"")
            buildConfigField("String", "PMTILES_URL", "\"https://tiles.mageride.lk/lk.pmtiles\"")
            buildConfigField("long", "INTEGRITY_CLOUD_PROJECT", "0L")
        }
    }

    testOptions {
        unitTests {
            // `android.os.Build`, `Log` and `Uri` are stubs in a local unit test and every member
            // throws by default. `:shared` and the driver app make the same call for the same
            // reason. Nothing here asserts against a framework return value; the flag is what
            // keeps a stub from taking down a test of our own code.
            isReturnDefaultValues = true
        }
    }

    // The SignalR client ships OkHttp, RxJava and Gson, each carrying its own JAR index and
    // licence files, and the APK can hold only one of each path. None of them is code — dropping
    // them changes nothing at runtime.
    packaging {
        resources {
            excludes += setOf(
                "META-INF/INDEX.LIST",
                "META-INF/DEPENDENCIES",
                "META-INF/LICENSE*",
                "META-INF/NOTICE*",
                // `com.uber:h3`'s desktop natives, ~1.5 MB of macOS and Windows binaries that an
                // APK can never load. They survived only because AGP's resource merger filters
                // `*.so` and these are not `.so` — see the `extractH3Natives` note above, which
                // is where the file Android DOES need comes from.
                "darwin-*/**",
                "windows-*/**",
            )
        }
    }
}

kotlin {
    jvmToolchain(libs.versions.jvmToolchain.get().toInt())
}

dependencies {
    implementation(project(":shared"))

    // C013 leaves the HTTP engine to the app on purpose — there is no multiplatform one.
    implementation(libs.ktor.client.okhttp)

    // D6' §5: "Client = SignalR Java client (Android)". The passenger half of the realtime-out
    // contract, and the reason this app is a live map at all.
    implementation(libs.signalr)
    // Its hub protocol's JSON tree type. `:shared`'s `MageRideJson` does the actual decoding —
    // see the catalogue note and `live/SignalRLiveHubTransport`.
    implementation(libs.gson)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons.extended)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)

    // D2' §0.1 — "a screen = a Compose @Composable route inside a NavHost/Scaffold".
    implementation(libs.androidx.navigation.compose)

    // Koin: `:shared` exports koin-core; these add `androidContext()` and `koinViewModel()`.
    implementation(libs.koin.android)
    implementation(libs.koin.androidx.compose)

    // D2' §0.3 — MapLibre GL Native over PMTiles. See the catalogue note on the `-opengl`
    // flavour; the default `android-sdk` artifact requires Vulkan and Play would filter out the
    // Android 8.0 handsets this platform is for. No `android-sdk-ktx`: it depends on the default
    // artifact and having both on one classpath fails `checkDuplicateClasses`.
    implementation(libs.maplibre.android)

    // The handset's own fix. Not for publishing — a passenger publishes nothing — but for the
    // R-06 geocell anchor, MAP-02's accuracy circle and §0.3's blue user dot.
    implementation(libs.play.services.location)

    // SCR-PA-017's "Scan driver's QR" (AL-22). CameraX for the viewfinder, `zxing:core` for the
    // decode — the same pair the driver app uses for SCR-DA-027, and for the same reason: the
    // `-android-embedded` artifacts ship a scanning Activity and a theme this app would have to
    // fight. `zxing:core` is pure Java with no camera and no Android dependency.
    implementation(libs.androidx.camera.core)
    implementation(libs.androidx.camera.camera2)
    implementation(libs.androidx.camera.lifecycle)
    implementation(libs.androidx.camera.view)
    implementation(libs.zxing.core)

    // Ride, package and location-request pushes. Inert until a `google-services.json` exists —
    // see the plugins block.
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)

    testImplementation(libs.kotlin.testjunit)
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    // C019's test kit, on the consumable configuration its build script publishes. Fakes,
    // fixtures and the scenario builders every module's tests share.
    testImplementation(project(path = ":shared", configuration = "testKitElements"))
}

// ---------------------------------------------------------------------------------------
// Static analysis. Config lives at the repo root — C011 put it there precisely so this module
// and the driver app would share one rule set rather than drifting into two.
// ---------------------------------------------------------------------------------------
detekt {
    buildUponDefaultConfig = true
    config.setFrom(rootProject.file("config/detekt/detekt.yml"))
    source.setFrom(files("src"))
    parallel = true
}

ktlint {
    version.set(libs.versions.ktlint)
    // Generated sources — BuildConfig and R — are not ours to format.
    filter {
        exclude { it.file.path.contains("/build/") }
    }
}
