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
    alias(libs.plugins.sqldelight)
}

// ---------------------------------------------------------------------------------------
// C018 kmp-local-db — the two on-device databases.
//
// `mobile_db_schema.md` §0.2: each app ships its OWN database file and its own table set —
// passenger gets §1 + §2, driver gets §1 + §3, and the two never merge even when both apps
// are installed on one handset (AL-08 is per app). Two SQLDelight databases is the faithful
// encoding of that: `MageRidePassengerDatabase.Schema.create()` physically cannot create a
// driver table.
//
// The §1 SHARED tables are authored ONCE, in src/commonMain/sqldelight/shared/, and are
// materialised into each database's own package by the Sync tasks below. That indirection is
// not decoration: SQLDelight derives a generated type's package from its path *under the
// source root*, so pointing both databases at one directory emits
// `…db.core.Command_outbox` twice into the same commonMain compilation and the module stops
// compiling ("Redeclaration", verified). Copying gives `…db.passenger.Command_outbox` and
// `…db.driver.Command_outbox` — one authored schema, two packages, no collision.
//
// Dialect: the SQLDelight default (SQLite 3.18). That is a floor, not an oversight —
// URD NFR-22 pins minSdk 26 and Android 8.0 ships SQLite 3.19, so `ALTER TABLE … RENAME
// COLUMN` (3.25), row-value IN (3.15) and UPSERT (3.24) are all off the table. Migration 1
// rebuilds its tables the portable way for exactly this reason; see the .sqm files.
// ---------------------------------------------------------------------------------------
private val sharedSqlDir = layout.projectDirectory.dir("src/commonMain/sqldelight/shared")

/** Where the §1 tables are staged for [app] — this directory is the SQLDelight source root. */
private fun sharedSchemaRoot(app: String) = layout.buildDirectory.dir("generated/sqldelight-shared/$app")

private fun sharedSchemaSync(app: String) = tasks.register<Sync>(
    "syncSharedSqlSchema${app.replaceFirstChar(Char::titlecase)}",
) {
    group = "sqldelight"
    description = "Materialises the mobile_db_schema.md §1 shared tables into the $app database's package."
    from(sharedSqlDir)
    // The path under the source root IS the generated package.
    into(sharedSchemaRoot(app).map { it.dir("lk/mageride/shared/db/$app") })
}

private val sharedSchemaTasks = listOf("passenger", "driver").associateWith { sharedSchemaSync(it) }

sqldelight {
    databases {
        // `mageride_passenger.db` — §1 shared + §2 passenger.
        create("MageRidePassengerDatabase") {
            packageName.set("lk.mageride.shared.db.passenger")
            srcDirs.setFrom(
                sharedSchemaRoot("passenger"),
                layout.projectDirectory.dir("src/commonMain/sqldelight/passenger"),
            )
        }
        // `mageride_driver.db` — §1 shared + §3 driver.
        create("MageRideDriverDatabase") {
            packageName.set("lk.mageride.shared.db.driver")
            srcDirs.setFrom(
                sharedSchemaRoot("driver"),
                layout.projectDirectory.dir("src/commonMain/sqldelight/driver"),
            )
        }
    }
}

// SQLDelight reads `srcDirs` into its own task inputs at configuration time, which drops any
// task dependency a provider would otherwise carry — without this the staging Sync simply never
// runs and both databases silently generate with the §1 tables missing (observed).
sharedSchemaTasks.forEach { (app, sync) ->
    val database = "MageRide${app.replaceFirstChar(Char::titlecase)}Database"
    tasks.matching { it.name.contains(database) && it.name.startsWith("generate") }
        .configureEach { dependsOn(sync) }
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

            // CBOR is the MQTT position payload (`realtime/mqtt-topics.md` §2.1, D6' §3.1).
            // `implementation`: PositionCodec is the only door to it, so no kotlinx.cbor type
            // reaches an app or the XCFramework. C017.
            implementation(libs.kotlinx.serialization.cbor)

            // Engine-agnostic Ktor. C013 (kmp-api-client) builds the client itself; the
            // engines live in the platform source sets below because there is no
            // multiplatform engine.
            implementation(libs.ktor.client.core)
            implementation(libs.ktor.client.content.negotiation)
            implementation(libs.ktor.client.logging)
            implementation(libs.ktor.serialization.json)

            // On-device SQLite (C018). `api`, not `implementation`: `SqlDriver`,
            // `ColumnAdapter` and the generated `MageRide*Database` types are all in this
            // module's public surface — an app builds the driver and holds the database.
            api(libs.sqldelight.runtime)
            api(libs.sqldelight.primitive.adapters)
            implementation(libs.sqldelight.coroutines.extensions)
        }

        commonTest.dependencies {
            implementation(libs.kotlin.test)
            implementation(libs.kotlinx.coroutines.test)
            implementation(libs.turbine)
            implementation(libs.koin.test)
            implementation(libs.ktor.client.mock)
        }

        // JVM-only tests of the Android actuals — and the only place a real SQLite engine is
        // reachable on this build host, so every schema, migration and query test lives here.
        // It is also the only source set that can read `backend/contracts/*.yaml` off disk,
        // which is why C019's contract checks live here rather than in commonTest.
        getByName("androidHostTest").dependencies {
            implementation(libs.sqldelight.sqlite.driver)
            implementation(libs.snakeyaml)
        }

        androidMain.dependencies {
            implementation(libs.kotlinx.coroutines.android)
            implementation(libs.ktor.client.okhttp)

            // C018. The driver is `implementation`: `PlatformDatabaseDriverFactory` answers a
            // common `SqlDriver`, so no androidx.sqlite or SQLCipher type reaches the apps.
            implementation(libs.sqldelight.android.driver)
            implementation(libs.sqlcipher.android)

            // D-30, Android half (C014). `implementation` on purpose: no Play Integrity type
            // appears in this module's public API — `PlatformAttestationProvider` takes a
            // `Context` and the cloud project number and answers a `String?` — so the apps never
            // compile against it. The iOS half needs no coordinate at all: App Attest comes from
            // Kotlin/Native's DeviceCheck platform library.
            implementation(libs.play.integrity)

            // H3 geocells (C017). `com.uber:h3` is a JNI wrapper over the reference C library and
            // ships no Kotlin/Native klib, so it can only live here; the jar carries android-arm
            // and android-arm64 natives beside the desktop ones, which is what lets the JVM tests
            // exercise the same code the app runs. `implementation`: H3JavaGrid is internal and
            // the public surface is our own H3Grid interface, so no com.uber type escapes.
            implementation(libs.h3)
        }

        iosMain.dependencies {
            implementation(libs.ktor.client.darwin)

            // C018. SQLiter under the hood; it links the system SQLite, which is why iOS gets
            // NSFileProtection rather than SQLCipher — see PlatformDatabaseDriverFactory.ios.kt.
            implementation(libs.sqldelight.native.driver)
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
// C019 kmp-test-kit — the test kit as a consumable artifact.
//
// The component's fence is "the test kit ships in a test source set / separate artifact — it must
// not leak into release app binaries", and `lk.mageride.shared.testing` lives in `commonTest`,
// which settles that half: nothing there is on any main compile classpath, so none of it reaches
// an app's release build or the XCFramework.
//
// The other half is that C025/C067/C076 have to be able to USE it. A Gradle test source set is not
// consumable by another project on its own, and `java-test-fixtures` is JVM-only and does not
// apply to a KMP module. So the kit is published as its own jar on its own consumable
// configuration, and an Android app module asks for it with:
//
//     testImplementation(project(path = ":shared", configuration = "testKitElements"))
//
// Scope, stated plainly: this covers the JVM/Android consumers, which is every consumer that
// exists before wave 4b. The iOS side compiles the same `commonTest` sources into the iOS test
// klibs; packaging those for an external consumer needs a Mac and a real iOS consumer to verify
// against, and neither exists yet (root CLAUDE.md "Build Host"). See the C019 handoff.
// ---------------------------------------------------------------------------------------
val testKitJar = tasks.register<Jar>("testKitJar") {
    group = "build"
    description = "Packages lk.mageride.shared.testing for app modules to depend on in their tests."
    archiveClassifier.set("test-kit")
    from(
        tasks.named("compileAndroidHostTest").map { compile ->
            compile.outputs.files.asFileTree.matching {
                include("lk/mageride/shared/testing/**")
                // The kit's OWN tests compile into the same output and have no business on an
                // app's classpath. Nothing the kit publishes ends in `Test` — `TestClock` and
                // `TestTime` start with it — so the suffix separates the two cleanly.
                exclude("**/*Test.class", "**/*Test\$*.class")
                // `testing/contract` is this module's own OpenAPI reader: internal, JVM-only, and
                // dependent on a YAML library that is not on this configuration. It checks the
                // kit; it is not part of it.
                exclude("lk/mageride/shared/testing/contract/**")
            }
        },
    )
}

val testKitElements: Configuration by configurations.creating {
    isCanBeConsumed = true
    isCanBeResolved = false
    description = "The C019 test kit, for an app module's unit tests."
    // A consumer needs what the kit's public surface is built on. `:shared` itself comes through
    // the app's ordinary dependency on it.
    dependencies.add(project.dependencies.create(libs.kotlin.test.get()))
    dependencies.add(project.dependencies.create(libs.kotlinx.coroutines.test.get()))
    dependencies.add(project.dependencies.create(libs.ktor.client.mock.get()))
}

artifacts.add(testKitElements.name, testKitJar)

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
