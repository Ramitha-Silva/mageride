# End-to-end runs

Scripted runs that drive the **real** stack — real containers, real brokers, real sockets — rather
than a test double of it. Nothing here is a unit test and nothing here belongs in a service's own
suite: an e2e exists to catch the failures that live in the seams between components.

```
e2e/
  walking-skeleton/     C025 — one booked Mode C ride end to end (the wave-2 milestone)
    run.sh              up -> migrate -> seed -> run -> assert -> down --volumes
    build.gradle.kts    :e2e:walking-skeleton, a Kotlin/JVM application on :shared
```

**Verify:** `bash e2e/walking-skeleton/run.sh`

**`tests/E2E` is the other one, and they are not rivals.** C120's Mode C suite, C121's Mode A/B,
tracker-plane and fleet suite, and C122's proxy/package/web-subview suite live there because the
manifest names `dotnet test tests/E2E` as their verify target and because they are a .NET test
assembly rather than a scripted run: they start five, nine and ten services in-process over
Testcontainers, which is what lets them drive the whole §11.12 matrix, a hundred accept races, four
last-will graces, real GT06/JT808/H02/NMEA frames on real sockets, and an AL-45 share token followed
out of an SMS into the browser page it was minted for — in a few minutes each. What is **only** here
is the property those suites cannot have — the platform driven through `:shared`, so a wiring mistake
between an app and a contract fails in CI rather than in somebody's hands — and that is worth keeping
whatever else exists.

## Rules

- **Drive the platform through `:shared`, not through curl.** The whole value of an e2e here is that
  it exercises the same api-client, the same `LiveHub` contract and the same `MqttTopics` /
  `PositionCodec` the apps do — so a wiring mistake between an app and a contract fails in CI rather
  than in someone's hands. `:shared` grew a `jvm()` target for exactly this (C025).
- **One command, from nothing.** `run.sh` brings the stack up, waits for health, seeds, runs, asserts
  and tears down. A run that needs a human to have done something first is a run nobody trusts.
- **Tear down `--volumes`.** Several platform rules are one-live-thing-per-actor (R-02), and the
  seeded driver is the same account every run — so a run that died mid-ride would poison every run
  after it. `KEEP_UP=1` keeps the stack for debugging and says so.
- **Assert the definition of done, and print each assertion as it passes.** A failure should name the
  link of the chain that broke; "the run failed" is not a diagnosis.
- **A workaround is documented at the point of the workaround.** Where the harness does something a
  real client would not — reading `dispatch.events` for an `offerId`, re-asserting presence because
  no heartbeat exists yet, minting its own MQTT session token — the code says which component owns
  the gap and when the workaround should be deleted. Recorded in the C025 handoff too.
- **Never reach into a database to fix state.** Everything the run needs must be reachable through
  the surface an app has. Where it is not, that is the finding.
