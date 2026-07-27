import org.jetbrains.kotlin.gradle.plugin.mpp.apple.XCFramework

// MageRide — KMP shared module (C011 kmp-module-scaffold).
//
// ADD §18.2: one Kotlin Multiplatform module carries every piece of shared business logic
// (models, API client, domain state machines, auth, geo) and all four apps depend on it.
// Platform UI is NOT here — Compose lives in apps/*-android, SwiftUI in apps/*-ios.
//
// The Gradle path is `:shared` even though the directory is shared/kmp — D7' §6/§7 spell
// the build commands `./gradlew :shared:build` and `:shared:assembleXCFramework`.
// settings.gradle.kts does the remapping.
//
// Host note (root CLAUDE.md): this repo is built on a Linux VPS. The three iOS targets are
// declared unconditionally because the macOS CI leg needs them, and gradle.properties turns on
// `kotlin.native.enableKlibsCrossCompilation`, so `compileKotlinIosArm64` type-checks
// src/iosMain here. Linking is a different matter — Kotlin/Native cannot produce an Apple
// binary off a Mac — so `assembleXCFramework` fails fast with a readable message on any
// non-macOS host rather than dying inside the linker.

plugins {
    alias(libs.plugins.kotlin.multiplatform)
    alias(libs.plugins.android.kmp.library)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.detekt)
    alias(libs.plugins.ktlint)
    // C018 (kmp-local-db) adds `alias(libs.plugins.sqldelight)` and the `sqldelight { }`
    // block. The plugin is in the catalog already; applying it here with no `.sq` file
    // would configure an empty database and generate nothing.
}

// The Swift-side module name: `import MageRideShared`. C085/C094 consume the XCFramework
// under this name, so changing it is a breaking change for both iOS apps.
private val frameworkName = "MageRideShared"

kotlin {
    jvmToolchain(libs.versions.jvmToolchain.get().toInt())

    // Every public declaration needs an explicit visibility and return type. This module is
    // the API surface for four apps and two languages; inference across that boundary is how
    // an accidental `internal`-looking public type ends up in the XCFramework.
    explicitApi()

    compilerOptions {
        // `expect class` is still flagged Beta (KT-61573) even though `expect fun` is stable.
        // C014 needs the class form: PlatformSecureStore and PlatformAttestationProvider have
        // genuinely different constructors per platform (a `Context` vs a Keychain service),
        // which an expect *function* cannot express. Without the flag every build prints five
        // warnings that say nothing actionable.
        freeCompilerArgs.add("-Xexpect-actual-classes")
    }

    android {
        namespace = "lk.mageride.shared"
        compileSdk = libs.versions.androidCompileSdk.get().toInt()
        minSdk = libs.versions.androidMinSdk.get().toInt()

        withHostTestBuilder {}.configure {
            // android.os.Build is a stub in a local unit test and every field throws by
            // default. PlatformInfo reads Build.VERSION; without this the Android actual
            // cannot be unit tested at all without pulling in Robolectric.
            isReturnDefaultValues = true
        }
    }

    val xcf = XCFramework(frameworkName)
    listOf(iosX64(), iosArm64(), iosSimulatorArm64()).forEach { target ->
        target.binaries.framework {
            baseName = frameworkName
            // Static: a dynamic framework would have to be embedded and re-signed by both
            // Xcode projects, and SwiftUI previews break on dynamic KMP frameworks.
            isStatic = true
            xcf.add(this)
        }
    }

    sourceSets {
        commonMain.dependencies {
            implementation(libs.kotlinx.coroutines.core)
            api(libs.kotlinx.serialization.json)
            api(libs.kotlinx.datetime)
            api(libs.koin.core)

            // Engine-agnostic Ktor. C013 (kmp-api-client) builds the client itself; the
            // engines live in the platform source sets below because there is no
            // multiplatform engine.
            implementation(libs.ktor.client.core)
            implementation(libs.ktor.client.content.negotiation)
            implementation(libs.ktor.client.logging)
            implementation(libs.ktor.serialization.json)
        }

        commonTest.dependencies {
            implementation(libs.kotlin.test)
            implementation(libs.kotlinx.coroutines.test)
            implementation(libs.turbine)
            implementation(libs.koin.test)
            implementation(libs.ktor.client.mock)
        }

        androidMain.dependencies {
            implementation(libs.kotlinx.coroutines.android)
            implementation(libs.ktor.client.okhttp)

            // D-30, Android half (C014). `implementation` on purpose: no Play Integrity type
            // appears in this module's public API — `PlatformAttestationProvider` takes a
            // `Context` and the cloud project number and answers a `String?` — so the apps never
            // compile against it. The iOS half needs no coordinate at all: App Attest comes from
            // Kotlin/Native's DeviceCheck platform library.
            implementation(libs.play.integrity)
        }

        iosMain.dependencies {
            implementation(libs.ktor.client.darwin)
        }
    }
}

// ---------------------------------------------------------------------------------------
// XCFramework — macOS only.
//
// `XCFramework("MageRideShared")` registers assembleMageRideSharedXCFramework (+ Debug and
// Release variants). D7' §6/§7 and .github/workflows/ci.yml both invoke the bare name
// `:shared:assembleXCFramework`, so that is the task the module must own.
// ---------------------------------------------------------------------------------------
val isMacOs = System.getProperty("os.name").startsWith("Mac", ignoreCase = true)

tasks.register("assembleXCFramework") {
    group = "build"
    description = "Assembles the release $frameworkName.xcframework for iOS. macOS hosts only."

    if (isMacOs) {
        dependsOn("assemble${frameworkName}ReleaseXCFramework")
    } else {
        doFirst {
            error(
                """
                :shared:assembleXCFramework requires macOS with Xcode.

                Kotlin/Native cannot link Apple binaries on ${System.getProperty("os.name")}.
                This host verifies the common + Android targets only:

                    ./gradlew :shared:testDebugUnitTest detekt ktlintCheck

                The iOS targets are built by the `build (ios)` leg of .github/workflows/ci.yml
                (macos-14) and on a developer Mac. See CLAUDE.md "Build Host".
                """.trimIndent(),
            )
        }
    }
}

// ---------------------------------------------------------------------------------------
// `testDebugUnitTest` — the wave-1 gate command.
//
// build/manifest.yaml, this component's DoD and .github/workflows/ci.yml all run
// `:shared:testDebugUnitTest`. That name comes from AGP's *variant* model
// (`com.android.library` + `androidTarget()`), which AGP 9 refuses to apply to a Kotlin
// Multiplatform project — see the plugin block. `com.android.kotlin.multiplatform.library`
// has no build variants, so the local unit-test task is `testAndroidHostTest` and there is no
// `debug` anything.
//
// This alias keeps the documented command working. It is not a stand-in for a weaker check:
// `androidHostTest` dependsOn `commonTest`, so this runs exactly what the old task ran —
// commonMain + commonTest + androidMain + androidHostTest on the JVM against the Android
// actuals. The manifest's verify_cmd should be retargeted at `:shared:testAndroidHostTest`;
// see the C011 handoff (micro-change-set).
// ---------------------------------------------------------------------------------------
tasks.register("testDebugUnitTest") {
    group = "verification"
    description = "Alias for testAndroidHostTest — the name build/manifest.yaml and CI use."
    dependsOn("testAndroidHostTest")
}

// ---------------------------------------------------------------------------------------
// Static analysis. The wave-1 gate is `./gradlew :shared:testDebugUnitTest detekt ktlintCheck`
// — `detekt` and `ktlintCheck` are unqualified, so Gradle runs them in every project that
// declares them. Today that is this module only.
// ---------------------------------------------------------------------------------------
detekt {
    // detekt's defaults assume src/main/kotlin + src/test/kotlin. A KMP module has
    // src/{commonMain,androidMain,iosMain,commonTest,androidHostTest}/kotlin, so point the
    // plain `detekt` task at the whole source tree instead of enumerating source sets that
    // C012-C019 will keep adding to.
    source.setFrom(files("src"))
    buildUponDefaultConfig = true
    config.setFrom(rootProject.file("config/detekt/detekt.yml"))
    parallel = true
}

tasks.withType<io.gitlab.arturbosch.detekt.Detekt>().configureEach {
    jvmTarget = libs.versions.jvmToolchain.get()
    reports {
        html.required.set(true)
        xml.required.set(false)
        sarif.required.set(false)
        md.required.set(false)
        txt.required.set(false)
    }
}

ktlint {
    version.set(libs.versions.ktlint.get())
    // Rules live in the repo-root .editorconfig (`ktlint_code_style = intellij_idea`), not
    // here — the IDE, ktlint and detekt then agree on one line limit and one style.
    ignoreFailures.set(false)
    filter {
        // Generated Kotlin (SQLDelight from C018 on) is never hand-formatted.
        exclude { it.file.path.contains("${File.separator}build${File.separator}") }
        exclude { it.file.path.contains("${File.separator}generated${File.separator}") }
    }
}
