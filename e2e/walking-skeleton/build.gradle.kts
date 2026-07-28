// MageRide — walking-skeleton end-to-end run (C025).
//
// A plain Kotlin/JVM program. It is deliberately NOT an Android instrumentation test: this build
// host is headless with no emulator image, and an e2e that could only run on a device would never
// run in CI either. What it *is* is the same code the apps use — `:shared`'s api-client, its
// `LiveHub` contract and its `MqttTopics`/`PositionCodec` — driven headlessly, so a wiring mistake
// between an app and a contract fails here rather than in someone's hands.
//
//   bash e2e/walking-skeleton/run.sh          # up, seed, run, assert
//   ./gradlew :e2e:walking-skeleton:run       # against an already-running stack

plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
    application
}

kotlin {
    jvmToolchain(libs.versions.jvmToolchain.get().toInt())
}

application {
    mainClass.set("lk.mageride.e2e.MainKt")
}

dependencies {
    // The whole point. `:shared` resolves its `jvm` variant here.
    implementation(project(":shared"))

    // The HTTP engine the module leaves to its consumer — OkHttp, the same one the Android apps
    // bind, so the harness exercises the engine that ships rather than a second one.
    implementation(libs.ktor.client.okhttp)

    implementation(libs.kotlinx.coroutines.core)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.datetime)

    // D6' §5 / §3: the two realtime clients the Android apps use. The passenger half is a real
    // WebSocket to `/hubs/live`; the driver half is a real MQTT 5 session against EMQX, presenting
    // a real session JWT to the real ACL.
    implementation(libs.signalr)
    implementation(libs.hivemq.mqtt)

    // `offer.created` on `dispatch.events` is the ONLY place the `offerId` a driver needs to accept
    // exists — no REST response carries it (C025 handoff, contract gap (a)). notification-svc
    // (C051) and the fanout push (C041) will read the same topic; until they exist, so does this.
    implementation(libs.kafka.clients)
}

tasks.named<JavaExec>("run") {
    // The run reads its endpoints and secrets from the environment; run.sh sets them.
    environment(System.getenv())

    // The REPOSITORY ROOT, not this project's directory, which is where Gradle would otherwise
    // put it. The harness shells out to read iam-svc's log (see OtpReader) and the natural way to
    // spell that command is `docker compose -f infra/docker-compose.skeleton.yml …`. From
    // `e2e/walking-skeleton` that path does not exist, and compose fails silently enough that the
    // symptom is "no OTP ever appeared" rather than "no such file".
    workingDir = rootProject.projectDir

    // A failed assertion must fail the Gradle build, and its message is the useful output.
    standardInput = System.`in`
}
