import javax.inject.Inject

// MageRide — Driver Android application shell (C067 driver-android-shell).
//
// The host every screen group plugs into: theme, trilingual resources, navigation, the map, the
// Koin graph and the two native services KMP cannot own (position publishing, FCM). It owns NO
// wireframe screen — C068–C075 do. What was here before was C025's walking skeleton; that slice
// declared itself throwaway and this replaces it.
//
// Verify: ./gradlew :apps:driver-android:testDebugUnitTest :apps:driver-android:assembleDebug

// NO `org.jetbrains.kotlin.android`. AGP 9 has built-in Kotlin support and refuses the plugin
// outright ("no longer required for Kotlin support since AGP 9.0"), the same way it refuses
// `com.android.library` in a KMP project — see shared/kmp/CLAUDE.md. The catalogue still declares
// the alias for anything on an older AGP.
//
// The `google-services` plugin is deliberately absent even though firebase-messaging is a
// dependency: it hard-fails the build without a `google-services.json`, and the Firebase project
// is C124's (D7' §13 secrets). See the C067 handoff — FCM compiles and registers here; it does
// not deliver until that file lands.
plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.detekt)
    alias(libs.plugins.ktlint)
}

// ---------------------------------------------------------------------------------------
// C017's H3 native library, lifted out of the jar and packaged as a real JNI lib.
//
// **Nothing in this app injects an `H3Grid` yet** — geocell subscription is the passenger's view
// (R-06), and a driver publishes position over MQTT rather than subscribing to cells. This block
// is here so that the day one does, it works, because `com.uber:h3` is ALREADY on this app's
// runtime classpath (`:shared`'s androidMain declares it `implementation`) and the failure it
// would otherwise produce is a process kill rather than a missing-class error anyone could read.
//
// `com.uber:h3` carries its natives as ORDINARY JAR RESOURCES — `/android-arm64/libh3-java.so`,
// `/linux-x64/libh3-java.so`, `/windows-x64/libh3-java.dll` and ten more — and
// `H3Core.newInstance()` unpacks whichever matches the running ABI to a temp file and
// `System.load`s it. That works on a desktop JVM, which is why `:shared`'s tests and the e2e
// harness exercise the real grid and pass; it can NEVER work inside an APK:
//
//   * AGP's java-resource merger drops every `*.so` outright — native libraries are
//     `MergeNativeLibsTask`'s job, not a resource's.
//   * `MergeNativeLibsTask` only recognises `lib/<abi>/*.so` inside a jar, which
//     `android-arm64/libh3-java.so` is not.
//
// So an APK ships 1.5 MB of macOS `.dylib` and Windows `.dll` (neither matches `*.so`) while the
// one file Android actually needs is silently absent, and the first `grid.cellAt()` dies with
// `UnsatisfiedLinkError: No native resource found at /android-arm64/libh3-java.so` on whichever
// dispatcher worker asked. The passenger app hit exactly that after a location grant.
//
// The fix is a jniLibs tree under the ABI names Android uses, loaded with `System.loadLibrary`
// (`H3Core.newSystemInstance()`); see `PlatformH3Grid.jvmShared.kt`, which keeps the unpack path
// as its fallback so the JVM target is unaffected.
//
// h3 4.4.0 ships `android-arm64` and `android-arm` and nothing else — there is no x86 or x86_64
// native, so an EMULATOR has no H3 whatever this task does. Any future caller here must treat a
// missing engine as a degraded feature rather than something to throw through a coroutine, the
// way `PassengerLiveMap.geocells` does.
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
    namespace = "lk.mageride.driver"
    compileSdk = libs.versions.androidCompileSdk.get().toInt()

    defaultConfig {
        applicationId = "lk.mageride.driver"
        // URD NFR-22 — Android 8.0. Not a preference; do not raise without a micro-change-set.
        minSdk = libs.versions.androidMinSdk.get().toInt()
        targetSdk = libs.versions.androidTargetSdk.get().toInt()
        versionCode = 1

        // Reaches the gateway as `X-App-Version`, where D-31's minimum-version gate reads it. The
        // gateway's floor is 1.0.0 (C008), so anything below this is answered 426 on every route.
        // `DriverEnvironment` reads it back out of BuildConfig — one number, not two.
        versionName = "1.0.0"
    }

    androidResources {
        // Si and Ta are not "extra" locales — CLAUDE.md makes all three mandatory, and En is the
        // fallback rather than the primary. Listing them keeps AGP's resource shrinker and the
        // Play language split from dropping the two that matter most here. (`localeFilters`, not
        // the `resourceConfigurations` this replaced: AGP 9 deprecated the older spelling.)
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
    // `DriverEnvironment` (di/DriverEnvironment.kt). No other file in this module may read
    // BuildConfig: a value that is read in two places is a value that gets overridden in one.
    // ---------------------------------------------------------------------------------
    buildTypes {
        getByName("debug") {
            isMinifyEnabled = false
            applicationIdSuffix = ".debug"

            // The replica edge, NOT the emulator loopback.
            //
            // `10.0.2.2` is the host machine's localhost as seen from inside an EMULATOR. On a
            // physical handset it resolves to nothing, so every call times out and the app shows
            // its generic "Something went wrong" — with no request ever reaching the edge to
            // explain it. Revert these to the 10.0.2.2 values for emulator work against the local
            // dev stack; the release variant below is unaffected either way.
            buildConfigField("String", "API_BASE_URL", "\"https://api.mageride.lk\"")
            // MQTT-over-WSS at the replica edge, which HAProxy passes through to EMQX at L4.
            //
            // **8084, NOT 8883.** 8883 is the TRACKER plane: `verify_peer` +
            // `fail_if_no_peer_cert`, so a client must present an X.509 certificate signed by the
            // device CA — which a handset does not have and cannot get. emqx.conf says where a
            // phone belongs: "the mobile plane keeps the JWT authenticator on 8084/1883". 8084 is
            // `verify_none` and the driver's session JWT is what authenticates it.
            //
            // TLS is real here: EMQX terminates it with the platform certificate (AL-63), which
            // carries `mqtt.mageride.lk`. Android refuses a self-signed one outright — no
            // click-through — so this only works because that certificate is trusted.
            buildConfigField("String", "MQTT_HOST", "\"mqtt.mageride.lk\"")
            buildConfigField("int", "MQTT_PORT", "8084")
            buildConfigField("boolean", "MQTT_TLS", "true")
            // PMTiles archive for the map style, on the tile host rather than the loopback: the
            // basemap otherwise renders blank on a device while the markers still draw.
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
            buildConfigField("String", "MQTT_HOST", "\"mqtt.mageride.lk\"")
            // 8084, NOT 8883. 8883 is the TRACKER plane: emqx.conf sets `verify_peer` +
            // `fail_if_no_peer_cert` on it, so a client must present an X.509 certificate signed
            // by the device CA — whose CN becomes its MQTT username — and it sets
            // `enable_authn = false` there, so the JWT authenticator is deliberately OFF. A driver
            // handset has neither: it fails the TLS handshake before MQTT starts, and its session
            // token would not help if it did.
            //
            // emqx.conf states the split outright: "the mobile plane keeps the JWT authenticator
            // on 8084/1883 (E-02, D-21)". 8084 is `verify_none` MQTT-over-WSS, and the driver's
            // JWT is what authenticates it there.
            buildConfigField("int", "MQTT_PORT", "8084")
            buildConfigField("boolean", "MQTT_TLS", "true")
            buildConfigField("String", "PMTILES_URL", "\"https://tiles.mageride.lk/lk.pmtiles\"")
            buildConfigField("long", "INTEGRITY_CLOUD_PROJECT", "0L")
        }
    }

    testOptions {
        unitTests {
            // `android.os.Build`, `Log` and `Uri` are stubs in a local unit test and every member
            // throws by default. `:shared` makes the same call for the same reason — see its
            // `withHostTestBuilder`. Nothing here asserts against a framework return value; the
            // flag is what keeps a stub from taking down a test of our own code.
            isReturnDefaultValues = true
        }
    }

    // HiveMQ ships a shaded client: Netty, RxJava and its own classes arrive in several jars, each
    // carrying its own JAR index and licence files, and the APK can hold only one of each path.
    // None of them is code — dropping them changes nothing at runtime.
    packaging {
        resources {
            excludes += setOf(
                "META-INF/INDEX.LIST",
                "META-INF/DEPENDENCIES",
                "META-INF/LICENSE*",
                "META-INF/NOTICE*",
                "META-INF/io.netty.versions.properties",
                // `com.uber:h3`'s desktop natives, ~1.5 MB of macOS and Windows binaries that an
                // APK can never load. They arrive through `:shared` and survived only because
                // AGP's resource merger filters `*.so` and these are not `.so` — see the
                // `extractH3Natives` note above, which is where the file Android DOES need
                // comes from.
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

    // D6' §3: "Driver App client = HiveMQ (Android) in a native foreground service".
    implementation(libs.hivemq.mqtt)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    // `LifecycleService` gives the foreground service a scope that dies with it, and
    // `ProcessLifecycleOwner` is how the shell tells foreground from background without an
    // Activity reference.
    implementation(libs.androidx.lifecycle.service)
    implementation(libs.androidx.lifecycle.process)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons.extended)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)

    // D2' §0.1 — "a Compose @Composable route inside a NavHost/Scaffold".
    implementation(libs.androidx.navigation.compose)

    // Koin: `:shared` exports koin-core; these add `androidContext()` and `koinViewModel()`.
    implementation(libs.koin.android)
    implementation(libs.koin.androidx.compose)

    // D2' §0.3 — MapLibre GL Native over PMTiles. See the catalogue note on the `-opengl` flavour.
    //
    // No `android-sdk-ktx`: it depends on the DEFAULT `android-sdk` artifact, and having both on
    // the classpath fails `checkDebugDuplicateClasses` on every `org.maplibre.android` class. Its
    // whole contribution is `awaitMap()`/`awaitStyle()` over the callbacks, which `MageRideMap`
    // already wraps once for the whole app.
    implementation(libs.maplibre.android)

    // The foreground service's fix source.
    implementation(libs.play.services.location)

    // AL-43 / SCR-DA-005 — the camera document-scanner with the draggable-corner crop. ADD names
    // `ImageCapture` for Android specifically, so this is the contract, not a choice of library.
    // The crop geometry and the edge-detect proposal are deliberately NOT here: they are pure
    // Kotlin in `capture/`, which is what makes them testable on this host.
    implementation(libs.androidx.camera.core)
    implementation(libs.androidx.camera.camera2)
    implementation(libs.androidx.camera.lifecycle)
    implementation(libs.androidx.camera.view)

    // E-01's ride pushes. Inert until a `google-services.json` exists — see the plugins block.
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)

    // AL-15's LankaQR fallback on SCR-DA-022 (C073). The ENCODER half of ZXing only — the driver
    // app scans no QR code at all (AL-34 removed the one path it had), so nothing here needs a
    // camera. See the catalogue note before swapping it for an `-android-embedded` artifact.
    implementation(libs.zxing.core)

    testImplementation(libs.kotlin.testjunit)
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    // C019's test kit, on the consumable configuration its build script publishes. Fakes,
    // fixtures and the scenario builders every module's tests share.
    testImplementation(project(path = ":shared", configuration = "testKitElements"))
}

// ---------------------------------------------------------------------------------------
// Static analysis. Config lives at the repo root — C011 put it there precisely so this module
// and C076 would share one rule set rather than drifting into three.
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
