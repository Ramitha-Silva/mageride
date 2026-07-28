package lk.mageride.shared.db

import java.security.SecureRandom

private val random = SecureRandom()

/**
 * `java.security.SecureRandom` — seeded from the kernel CSPRNG on every Android release this app
 * supports (API 26+) and on every JVM the harness runs on, so no manual seeding is wanted here.
 * Never `kotlin.random.Random`: a database key drawn from a seeded PRNG is derivable from any
 * other value it produced.
 *
 * Shared between the Android and JVM targets (C025) — it was identical in both, and a database
 * key is not something to keep two copies of.
 */
public actual fun secureRandomBytes(size: Int): ByteArray = ByteArray(size).also(random::nextBytes)
