package lk.mageride.e2e

import java.util.concurrent.TimeUnit

/**
 * Reads a one-time code out of iam-svc's log.
 *
 * C020's dev SMS sender writes `[dev-sms] OTP for +947… (en) is 123456` at Information and its
 * KDoc names this run as the reason: "C025's scripted end-to-end run reads the code out of the
 * container log". The code is deliberately **not** in the HTTP response — a login endpoint that
 * returned the OTP would be a login endpoint with no second factor at all — so a log read is the
 * only honest way for a script to sign in.
 *
 * The command is configurable ([Environment.otpLogCommand]) because how you reach the log depends
 * on how the stack is running: compose here, `kubectl logs` somewhere else.
 */
internal class OtpReader(private val environment: Environment) {

    /**
     * The most recent code logged for [phone].
     *
     * Polls, because the log line is written by a different process and a request that has just
     * returned `200` may still be a few milliseconds ahead of it.
     */
    fun await(phone: String, timeoutMs: Long = 20_000): String {
        val deadline = System.currentTimeMillis() + timeoutMs
        var lastSeen: String? = null

        while (System.currentTimeMillis() < deadline) {
            lastSeen = latest(phone)
            if (lastSeen != null) return lastSeen
            Thread.sleep(POLL_MS)
        }

        error(
            "No OTP for $phone appeared in the iam-svc log within ${timeoutMs}ms. " +
                "Is Sms__Provider=dev and is the log command right?\n  ${environment.otpLogCommand}",
        )
    }

    private fun latest(phone: String): String? {
        val process = ProcessBuilder("sh", "-c", environment.otpLogCommand)
            .redirectErrorStream(true)
            .start()

        val output = process.inputStream.bufferedReader().use { it.readText() }
        process.waitFor(READ_TIMEOUT_SEC, TimeUnit.SECONDS)

        // Last match wins: the run signs in two accounts, and the driver's code must not be read
        // as the passenger's.
        return PATTERN.findAll(output)
            .lastOrNull { it.groupValues[1] == phone }
            ?.groupValues
            ?.get(2)
    }

    private companion object {
        /** Matches `DevLoggingOtpSender`'s format string exactly. */
        val PATTERN = Regex("""\[dev-sms] OTP for (\+\d+) \([a-z]{2}\) is (\d+)""")
        const val POLL_MS = 500L
        const val READ_TIMEOUT_SEC = 20L
    }
}
