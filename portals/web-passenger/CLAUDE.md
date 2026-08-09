# Passenger Web Subview (C117) — `passenger.mageride.lk`

Next.js 16 (App Router) + TypeScript + React 19, styled **only** with Tailwind through
`@mageride/tailwind-preset` (AL-52). npm workspace member `@mageride/web-passenger` under `portals/`.

**Verify:** `npm --prefix portals run lint --workspace web-passenger && npm --prefix portals run test --workspace web-passenger && npm --prefix portals run build --workspace web-passenger`

`backend/contracts/public-bff.yaml` is normative for the wire shapes and wins over this file and over
the code. `specs/wireframes/web_passenger.html` is the layout baseline (six screens, SCR-WT-001…006).

## What this is

The six no-login pages a **package recipient**, a **proxy rider** and an **unregistered rider being
asked where they are** reach from an SMS (AL-04, AL-44, URD Epic 25). Not a product: a *subview of
the Passenger App* for people who do not have it.

| Screen | Route | Scope | Spec |
|---|---|---|---|
| SCR-WT-001 landing / token gate | `/track?token=…` | `package_recipient` | US-25.1 |
| SCR-WT-002 package track | `/t/{token}` | `package_recipient` | US-25.1/25.2, US-20.5 |
| SCR-WT-003 confirm pickup | `/p/{token}` | `pickup_confirm` | US-25.3, AL-45, P-02 |
| SCR-WT-004 ride track | `/p/{token}` | `proxy_rider` | US-25.1/25.4/25.5, US-8.21/8.22 |
| SCR-WT-005 delivered / receipt | `/t/{token}` (`Delivered`) · `/p/{token}` (a finished ride with a receipt) | `package_recipient` · `proxy_rider` | US-25.6 |
| SCR-WT-006 expired / invalid | any dead token, and `/` | — | US-25.1 |

`/track?token=…` is the URL notification-svc puts in the SMS (D1' Δ 2026-07-05). `/t/…` and `/p/…`
are the wireframe's own two URL shapes, kept so the address bar says which kind of link somebody is
holding. **All three call `trackScreen`** (`src/server/screen.tsx`) and dispatch on the token's
scope — the path never decides the screen.

## The four fences, and how each is held structurally

- **No app chrome, no login; the token is the credential.** `Shell` is a 46px brand strip and an
  optional app-download strip, and that is the entire frame: no nav, no account menu, no way to
  reach a second ride, because the token addresses exactly one.
- **Nothing is rendered before the token validates.** The token is redeemed on the **server** inside
  `trackScreen` before a tree exists. `<DeadEnd>` takes a translator, a locale, a path and a store
  URL — **no snapshot** — so "an expired token renders SCR-WT-006 with no ride data in the DOM" is a
  property of a signature rather than care taken by a caller. public-bff holds the other half: its
  404/410 is produced *before any ride row is read*, so the payload this page never renders was never
  fetched. `test/fences.test.ts` asserts the prop list; `test/screens.test.tsx` greps the rendered
  document for the driver, the plate, the number, the sender and the code.
- **The driver contact is a plain `tel:` link.** AL-48 removed `POST /public/track/{token}/call`, the
  ride-scoped proxy-DID lease and the confirm-your-number step *in full*; the snapshot carries
  `driver.phone` and the page dials it (US-26.3). The fences test refuses any path containing
  `/call`, by name — the same guard public-bff refuses to *start* under.
- **Declining a location request transmits no GPS.** Four components hold it: the copy says so before
  the reader decides; `declinePickupLocation(token)` and `declinePickup(token)` have no parameter for
  a coordinate; the request carries no body; ride-svc's statement has no `resolved_geo`. Asserted on
  the signatures, on the request, and on the behaviour (`test/pickup.test.ts` proves Decline never
  touches the Geolocation API).

## The load-bearing decisions

### The browser never reaches the platform, and this surface has a different reason

The share token is already in the visitor's address bar, so — unlike the two operator consoles —
hiding a credential is not the point. Keeping the **platform** out of the browser is:
`passenger.mageride.lk` is the only host a phone opened from an SMS talks to. One origin, one TLS
handshake, no gateway address in any shipped script, no CORS policy to widen on a no-login endpoint,
and nothing for a captive portal to break separately. `src/api/http.ts` is `server-only`;
`test/fences.test.ts` allows `process.env` in exactly one module and `fetch` in exactly two — the
transport, and the live hook, whose URL must be relative.

**There is no `NEXT_PUBLIC_*` variable and there must not be one.** `scripts/check-bundle.mjs`
searches the emitted client chunks for each server-only variable *by name*, because a value inlined
into a chunk is invisible in review — the source still reads `process.env.MAGERIDE_API_BASE_URL`.

### There is no cookie and no `localStorage` (D6' I-29.1)

So the language switch is **`?lang=` on the URL**, not a preference: three links inside a `<details>`
disclosure, no JavaScript, no client component, no state. Appearance follows the OS and nothing else.
And there is no third language source — the other two portals read the signed-in member's
`iam.users.language`, but **nobody is signed in here**, and resolving a language off the ride would
mean reading the *booker's* preference to somebody who is not the booker.

### The ≤1 s spinner and "no data before validation" are the same mechanism

`loading.tsx` is Next's Suspense fallback for each of the three routes, so a branded frame and a
spinner stream immediately while the server redeems the token, and the first byte about a ride is
written only after public-bff has said the token is live.

**That is also why nothing redirects.** An earlier shape sent the two proxy scopes to `/p/{token}`
with `redirect()`; once the spinner has streamed, the headers are gone and Next can only finish a
redirect as a meta refresh — a spinner, a blank, and a second spinner for a rider whose car is
moving. Every scope renders in place. `test/fences.test.ts` fails on a `redirect(`.

### One live feed per screen, read through context

`LiveFeed` opens one `EventSource` against this origin's own `/api/live/{token}` proxy and provides
it to the map **and** to the SOS button. Two hooks would be two connections against a per-token rate
bucket, and handing the SOS the server-rendered snapshot instead would give it a fix from whenever
the page was opened. The children stay server-rendered and pass straight through.

**Reconnecting is the browser's job.** An `EventSource` reopens on its own and sends `Last-Event-ID`;
public-bff writes the cursor into every frame's `id:` and honours the header identically to `?since`.
The only thing that can break it is the proxy dropping the header, so that is what `test/live.test.ts`
asserts. A dropped stream is the **normal** case — `StreamMaxDuration` closes every connection after
five minutes so a revocation reaches somebody who left a tab open — so a page that treated one as a
failure would show an error every five minutes on a perfect delivery.

The `?since` poll fallback is not for old browsers. It is for an intermediary that buffers a response
body and turns SSE into a feed that arrives once, when it ends — which no header of public-bff's can
reach. After two failed opens the hook switches to polling and stays there.

**`resolved` is a separate frame from the status that says so**, and the page uses it to call
`router.refresh()`: the **server** re-reads the token and renders SCR-WT-005 or, if safety-svc
revoked it at trip end, SCR-WT-006. A client-side transition to a receipt would be the page deciding
it was still entitled to data nobody had re-checked.

### The writes are server actions that return a **key**, never a sentence

The Fleet Portal composes result copy inside its actions because its client components cannot hold a
translator. This surface's can — `@/i18n` is framework-free, so a client component is handed a locale
string and builds its own — which is also what gives live copy like "updated 12s ago" somewhere to
come from. `problem.title` is never returned in any form (`_shared.yaml`: "never localised").

**No `Idempotency-Key` is sent, deliberately.** public-bff derives a better one from the business
fact: `pickup:{verb}:{token}` is stable for ever, because a location request can be answered once and
a retried tap should replay rather than read a refusal, while `sos:{window}:{token}` is *windowed*, so
a second genuine emergency twenty minutes later is not a replay of the first.

### Rules for a screen component

- **Every string goes through the translator, in all three files, in the same change.** `en.ts`
  defines the key set and the other two are typed against it, so a missing translation is a compile
  error; the lint rule stops a literal reaching JSX.
- **A client component takes a `locale`, not label props.** React cannot serialise a function across
  the boundary, and thirty strings is not a prop list.
- **`sm:` is 375px, `md:` 768px, `lg:` 1024px.** Mobile-first: the base styles *are* the phone.
  `test/fences.test.ts` fails on any fixed width above 375px and on `w-screen`.
- **The map is `LazyTrackMap`, never `TrackMap` directly.** MapLibre is larger than the rest of the
  application put together and three screens never draw a map.
- **A page renders no `async` child component.** `trackScreen` is an async *function* the routes
  return: a tree with an unresolved async child renders as nothing under `@testing-library`, which is
  a test that cannot be written rather than one that fails.

## Spec gaps found, and what was done about each

| Gap | What was done |
|---|---|
| **`GET …/receipt` has no PDF.** `public-bff.yaml` offers HTML and `application/pdf`; the implemented route answers JSON only (`ReceiptAsync` returns `Ok<ReceiptResponse>`, no negotiation, nothing in that assembly renders a document) | "Download receipt" is `window.print()` over `print-hidden` chrome — the arrangement the Fleet Portal already reached for SCR-FP-009's PDF. Every figure on the paper is one public-bff returned; composing a second receipt document here would be a second statement about the same delivery |
| **`startOtp` is absent on every proxy ride** — no endpoint on the platform issues one | The card renders one sentence instead of four empty boxes. It draws the real card the moment a code exists |
| **A package snapshot has no ETA and no parcel size**, so the wireframe's landing card cannot carry two of its three rows | The card carries From, Status and Driver — three things the snapshot knows |
| **`dropoff` is omitted on every parcel** (`rides.rides` stores a `dropoff_geo` and no address), so SCR-WT-002 has neither a line to print nor a point to draw | The map carries the vehicle alone |
| **No driver rating on any public snapshot**, though the wireframe draws ★4.7 / ★4.8 | Omitted |
| **No app-store URL exists anywhere in the platform's configuration** | Two optional variables. With neither set the control is not drawn rather than drawn and dead; SCR-WT-006 is still a complete page without it |
| **Lighthouse mobile could not be run** — this build host has no Chrome | The no-horizontal-scroll half is enforced as a test over the tree instead, with `overflow-x-clip` (not `-hidden`, which would break the sticky bar) as a backstop |

## Deliberate wireframe deviations

1. **The landing card is From / Status / Driver**, not From / Size / ETA — see the gaps above.
2. **No ★ rating on either driver card** — no public snapshot carries one.
3. **SCR-WT-002's driver row shows the plate and vehicle type, not "· ETA 12 min"** — `etaMin` is on
   the proxy-rider variant only.
4. **SCR-WT-004's driver card has no call control**; the wireframe puts "📞 Call driver" in the
   button row beside SOS, and two controls dialling the same number would be one redundant thing to
   find in an emergency (and the same accessible name twice on one page).
5. **SOS asks twice.** The sketch draws it as one of a pair of buttons a thumb's width from "Call
   driver", on a phone, in a moving vehicle; a single press that SMSed somebody would be pressed by
   accident, and a false alert costs two messages and the booker's attention.
6. **SCR-WT-005 also answers a finished proxy ride**, which the sketch draws only for a parcel. D2
   heads the screen "Delivered / Trip Summary" and public-bff's receipt is kind-agnostic, so a rider
   whose link outlives the trip gets a summary rather than a tracker watching a stopped vehicle. The
   **receipt decides**: `Receiptable` excludes the six cancellation and no-show terminals, and a
   `null` falls through to the tracker's own "This ride has ended".
7. **A live indicator sits under the map** on SCR-WT-002/004. Not in the sketch, and the DoD's
   reconnect requirement is invisible without it: a page that quietly stopped updating looks exactly
   like a vehicle that stopped moving.

## Configuration

`.env.example` documents every variable. `MAGERIDE_API_BASE_URL` (the C008 gateway origin) is
required; absent, every page answers 503 rather than 500. `WEB_PASSENGER_MAP_STYLE_URL` (D-14's
`tile-cdn`) is optional — unset, the markers render on an empty canvas and the screen says so, because
a missing basemap must not read as a missing driver. `WEB_PASSENGER_ANDROID_APP_URL` /
`WEB_PASSENGER_IOS_APP_URL` are optional; the `User-Agent` picks between them.

`output: 'standalone'`, so the container entrypoint is
`node .next/standalone/portals/web-passenger/server.js`, not `next start`.
