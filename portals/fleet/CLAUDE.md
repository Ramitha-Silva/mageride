# Fleet Portal (C111 shell + C112 auth/org/payout + C113 vehicles/drivers/trackers + C114 dashboard/map/analytics + C115 scheduling/billing) — `fleet.mageride.lk`

Next.js 16 (App Router) + TypeScript + React 19, styled **only** with Tailwind through
`@mageride/tailwind-preset` (AL-52). npm workspace member `@mageride/fleet-portal` under `portals/`.

**Verify:** `npm --prefix portals run lint --workspace fleet && npm --prefix portals run test --workspace fleet && npm --prefix portals run build --workspace fleet`

`backend/contracts/fleet.yaml`, `fleet-billing.yaml`, `fleet-health.yaml` and `iam.yaml` are
normative for the wire shapes and win over this file and over the code.
`specs/wireframes/web_fleet.html` is the layout baseline (12 screens, SCR-FP-001…012).

## What C111 is

The **application shell**: session, org-scoped navigation, the data layer to fleet-svc, and the page
chrome the twelve screens sit inside. C111 owns no wireframe screen ID — the sign-in *form* is here
because there is no session to hand a screen without one, but SCR-FP-001 itself (sign-up, the
verification and reset copy, identity link/unlink) is C112's, which drops its own
`app/(portal)/…/page.tsx` beside the catch-all placeholder, as do C113…C116.

## What C112 is — SCR-FP-001, SCR-FP-002 and SCR-FP-002a

The first three screens: sign-in **and sign-up**, the organisation, its team, and the Owner-only
bank & payout profile. `app/login` + `app/signup` (one card, two tabs, two routes),
`app/(portal)/org/{setup,team,payout}`, `src/api/{org,payout}.ts`,
`src/server/{org,payout}-actions.ts` and `src/components/{auth,org,payout}/`.

Three things it added to the shell, each on one route's account:

- **`apiFetch` sends a `FormData` body as multipart** rather than JSON-encoding it.
  `POST …/payout-profile/documents` is the portal's one multipart route (AL-49), and
  `next.config.ts` raises `serverActions.bodySizeLimit` to fleet-svc's own `DocumentMaxBytes` (8 MB)
  so a photographed passbook page is not refused before the service that owns the rule sees it.
- **`canMutate(…, { allowsNoOrganisation: true })`** — the control-level twin of the manifest's
  `allowsNoOrganisation`, set on `POST /v1/fleets` and nowhere else. It is the one mutation an
  account with no membership can make, because it is the call that creates the membership.
- **`/signup` is public** — `web_fleet.html`'s own address bar for SCR-FP-001.

### The AL-49 gate lives in `src/api/payout.ts`, and C113 imports it

BR-31.1 makes `PUT …/classification {mode_b_billing:'paid'}` answer `409 payout-profile-not-verified`
while the org profile is not `verified`. The **control** is SCR-FP-004's ("Service payment ·
Free / Paid", C113); the **fact** is this screen's. So `canSetPaidServicePayment(profile)` and
`PAID_SERVICE_PAYMENT_BLOCKED_KEY` are exported as a pair — one predicate, one sentence — and
SCR-FP-004 disables the Paid option and explains it without re-deriving anything.
`test/payout.test.ts` pins both against `fleet.yaml`.

### What SCR-FP-002 and SCR-FP-002a cannot do, and say so

Four affordances the wireframe draws have **no route on any contract**, and each is a sentence on
the screen rather than a control that posts nowhere (all four are in the C112 handoff):

1. **Editing an organisation.** `POST /v1/fleets` creates and `GET /v1/fleets/{id}` reads; there is
   no `PUT`. The KYC fields are rendered as the record an officer is reading.
2. **Org-level KYC documents** ("⬆ Upload KYC documents (BR, owner ID)"). fleet-svc's only document
   route is the AL-49 payout evidence; `registry.documents`' fleet kinds are AL-50's four
   **per-vehicle** slots.
3. **An organisation language.** `registry.fleets` has no such column. The control is real and sets
   *this console's* language, and its caption says exactly that.
4. **Removing or re-seating a member.** `POST …/members` provisions; nothing deletes or changes a
   seat. And nothing emails the invitee — there is no fleet-org template on the platform — so the
   invite form says to tell them out of band.

Shared infrastructure (C103) — use it, do not re-implement it: `@mageride/tailwind-preset`
(D2 §0.2 tokens), `@mageride/ui` (button/field/chip/pill/table/modal/toast/tabs/dropzone),
`@mageride/eslint-config` (`react` flat config). `@mageride/i18n` carries what **every** surface
shares; this portal's own copy lives in `src/i18n/messages/{en,si,ta}.ts`.

## The six load-bearing decisions

### 1. The gate is `proxy.ts`, and it is not in a layout

An App Router **layout is reused, not re-rendered, when navigation moves between its children**.
A guard in `app/(portal)/layout.tsx` would run on the first page load of a session and never again
— which is exactly the case a route guard exists for. `proxy.ts` (Next 16's `middleware.ts`; Node
runtime, so `process.env` is read at run time and not baked into the image) runs on every request
including the RSC fetch a client-side navigation makes.

It produces **two** refusals, and they are different pages because they are different facts.
`/denied` calls `forbidden()` — a real 403, because the caller's seat does not carry that screen.
`/pending` renders inside the chrome with a 200, because the organisation is still in verification:
that is not a refusal of the person, everybody in the org is waiting on the same officer, and the
useful thing on the page is what *is* open meanwhile.

### 2. There is no session endpoint, so the shell composes one from two reads

`GET /v1/admin/session` is the Admin Portal's whole answer. **fleet-svc has nothing like it**, so:

- `GET /v1/me/permissions` (iam-svc) — the caller's URD §2.3 rows **plus `fleetId` and
  `fleetRole`**, read from `iam.fleet_members` rather than from the token, which carries only the
  most privileged of a person's memberships. It is `IPermissionEvaluator`'s output with URD §2.1's
  fleet sub-model already applied: a Viewer's rows carry `read` and no `write`, and a Manager's
  `fleet-billing` row carries `read` and no `write`.
- `GET /v1/fleets/{fleetId}` (fleet-svc) — the organisation, and the only place `status` can be
  read. Deliberately not gated on approval: "a PENDING org's owner needs to see that it is pending".

`getSession()` is `cache()`d, so a render makes the pair once; `proxy.ts` caches the same pair per
bearer for `FLEET_PORTAL_SESSION_CACHE_SECONDS`.

### 3. The nav manifest is local, and every entry declares what its own routes declare

`src/server/routes.ts` is the only screen table on the platform that a server does not send. It has
to be — nothing answers "which Fleet Portal screens may this caller open" — so each entry carries
three declarations transcribed off the endpoints it fronts, never derived here:

| declaration | where it comes from | what checks it |
|---|---|---|
| `area` + `needs` | the URD §2.3 row the endpoints are gated on | `GET /v1/me/permissions` answers it |
| `minimumFleetRole` | the route's own `RequireFleetSubRole(...)` | `test/routes.test.ts` parses the C# |
| `requiresApprovedOrg` | the route sitting inside a `RequireApprovedFleet()` group | same test, same files |

**Not one screen more than fleet-svc blocks.** `FleetVehiclesGroup` and `FleetAssignmentsGroup`
carry the approval gate on the *group*, so a pending org cannot even list its vehicles and those
screens are dropped; the ops group (`/map`, `/analytics`, `/schedules`, `/geofences`,
fleet-health's `/health`) does not, so those screens stay open and the individual writes are what
get refused. Refusing something the platform allows is this portal inventing a refusal.

**It is still not authorization** (AL-06/US-21.1): `FleetAccessFilter` re-reads the membership on
every request and every endpoint re-decides.

### 4. Every read is org-scoped, and a screen never holds an organisation id

`read({ org: '/vehicles' })` becomes `/v1/fleets/{the caller's own fleetId}/vehicles`. There is no
parameter, no prop and no query string by which a screen can name a different organisation —
`src/api/client.ts` is the only module that writes a `{fleetId}` into a URL a screen asked for, and
`test/fences.test.ts` enumerates the tree to keep it that way. The server refuses anyway (RLS, then
`403 not-fleet-member`); this side makes the attempt unrepresentable.

### 5. Mutations declare the row they need, and a Viewer's are refused before they leave

`mutate()` requires `requires: { area, requiresApprovedOrg? }` and checks it against the caller's
own evaluated permissions before sending. That is the second half of "a Viewer session renders no
mutating control anywhere": if one is ever drawn by mistake, pressing it changes nothing. Every
mutation carries an `Idempotency-Key` — `_shared.yaml` marks the parameter `required: true`.

### 6. The browser never holds a token and never sees the platform

Every call leaves the Next server. The session lives in httpOnly cookies; `src/api/http.ts` is the
only module that calls `fetch`, and it is `server-only` so a client component importing it fails to
compile. There is no `NEXT_PUBLIC_*` variable and there must not be one. The client bundle also
never gets `src/server/routes.ts` or `src/server/access.ts` — `SideNav` resolves the current entry
from the paths it was handed.

## AL-07's three sign-in arms, and why the two federated ones look like that

`/v1/auth/password`, `/v1/auth/google`, `/v1/auth/apple`. **No MFA (AL-37)**, no phone OTP, no
`X-Platform` header — `AuthEndpoints.RefuseApps` refuses an `android`/`ios` platform on all three.

Both provider routes take an **ID token**, not an authorization code. Redeeming a code needs a
client secret, and the only route that holds one (`POST /v1/admin/auth/login {googleAuthCode}`)
forces `app=admin`. So the browser asks the provider for a signed ID token with
`response_type=id_token` (Google) / `code id_token` (Apple), the provider POSTs it back with
`response_mode=form_post`, and this process relays it to iam-svc, which owns the JWKS trust. The
portal holds no secret and validates nothing.

That POST is **cross-site**, which is why `mr_fleet_oauth_state` is the one `SameSite=None` cookie
here: a `Lax` cookie is not sent on a cross-site POST, so the CSRF check would fail closed on every
sign-in. `None` requires `Secure`, so federated sign-in does not complete over plain HTTP — the
password arm does, and `.env.example` says so.

## Rules for a screen component (C113…C116)

- **Add `app/(portal)/<path>/page.tsx`** at the path your entry has in `src/server/routes.ts`. It
  takes precedence over the catch-all automatically. A *new* nav entry has to be added to the
  manifest in the same change, and `test/routes.test.ts` will hold its three declarations against
  fleet-svc's.
- **Call `read()` / `mutate()` / `download()` from `@/api/client`, with `{ org: … }`.**
  `test/fences.test.ts` fails on a raw `fetch`, on `apiFetch` outside the three modules that own it,
  on a `/v1/**` path outside the seven the shell needs, and on any second place that builds a
  `/v1/fleets/{id}` URL. `download()` (Δ C115) is the third and is for a route that answers a
  **document** rather than a body — one caller, SCR-FP-010's invoice CSV/PDF.
- **A label prop is a string, never a function.** React refuses to serialise a function across the
  server/client boundary, so `labels={{ done: (x) => t('…', { x }) }}` is a runtime error on the
  page that renders it. A sentence that depends on the *result* of an action is composed **in the
  action**, which runs on the server and already has the translator and the Colombo formatter —
  `VehicleActionState.added`, `TrackerActionState.bound`, `DriverActionState.done`, the two bulk
  panels' `jobProgress`/`jobFailures`, `ScheduleActionState.booked` and `TopupView`.
  `test/fences.test.ts` asserts it over every `'use client'` component's props, with three
  documented exemptions: a **server action** (which serialises as a reference, not a body) and the
  `reset` Next hands its own error boundaries.
- **Gate a mutating control on `canMutate(session, area)`,** never on the sub-role. The one
  exception is `canManageTeam()`, and its comment says why.
- **Every string goes through the translator, in all three files, in the same change.** `en.ts`
  defines the key set and the other two are typed against it, so a missing translation is a compile
  error. The lint rule stops a literal reaching JSX.
- **Render a failure with `<ProblemPanel>`.** Never `problem.title` — `_shared.yaml` says it in as
  many words: "Short English summary for developers. Never localised."
- **`sm:` is 375px, `md:` 768px, `lg:` 1024px, and there is no `xl:`.** The preset replaces
  Tailwind's breakpoints; D2 §FP defines three widths and the portal gets three.
- **Dark mode is the `.dark` class on `<html>`, set once in `app/layout.tsx`.** Do not add a second
  mechanism.
- **`@mageride/ui` is importable from a server component.** `Field`/`Input`/`Select`/`Textarea`,
  `Tabs`, `Modal`, `Toast` and `Dropzone` carry `'use client'`; the rest render on the server.
- **Never surface a Mode C option (AL-03).** `test/fences.test.ts` greps the whole tree for it and
  for a `/v1/rides` or `/v1/dispatch` call.

## What the platform cannot do on SCR-FP-001

Three of the wireframe's own affordances have **no route on any contract**, and the screen states
each in words rather than drawing a control that posts nowhere:

1. **Sign-up.** `POST /v1/fleets` registers an *organisation* and is gated on already holding
   `fleet_owner`. The only two things that grant it are an existing Owner's
   `POST /v1/fleets/{id}/members` and a Super Admin's role grant. A new operator cannot create an
   account from this screen. The **Create account** tab explains those two paths instead.
2. **Email verification and password reset.** iam-svc has nine auth operations and none of them
   verifies an address or resets a password.
3. **Identity link/unlink.** `iam.federated_identities` is written by a provider sign-in; no route
   reads, adds or removes a row.

`test/auth-screen.test.tsx` asserts the sign-up panel holds no form, no input and no submit, so a
control cannot be added to it without the reason being revisited. See the C111 and C112 entries in
`build/progress.md`.

## What C113 is — SCR-FP-004, SCR-FP-005 and SCR-FP-006

The operating half of the console: **vehicle onboarding** with AL-50's four named document slots
and AL-51's Service payment, **driver assignment**, and **tracker binding**.
`app/(portal)/{vehicles,drivers,trackers}`, `src/api/{vehicles,drivers,trackers}.ts`,
`src/server/{vehicle,driver,tracker}-actions.ts`, `src/components/{vehicles,drivers,trackers}/` and
`src/i18n/format.ts`. It added no nav entry — C111 declared all three — and no shell behaviour.

`app/(portal)/vehicles/onboard/page.tsx` is a 308 to `/vehicles`: `web_fleet.html`'s address bar
for SCR-FP-004 is `/vehicles/onboard`, and one screen with two URLs would give the nav highlight
two places to be right.

### AL-50 is four cards mounted from a literal list

`VEHICLE_DOCUMENT_SLOTS` in `src/api/vehicles.ts` carries the four — registration copy, insurance
certificate, revenue license, route permit — with the **wire** kind each posts under.
`VehicleDocumentPanel` maps that list, never the server's answer, so "no generic dropzone" is a
property of the code: a fifth slot needs a fifth entry, and `DocumentSlotCard` takes its `kind` as
a prop rather than reading one from a control. The stored kinds (`registration`, `permit`) and the
wire kinds (`registration_copy`, `route_permit`) are **two lists**, and fleet-svc refuses a stored
name in an upload — `test/vehicles.test.ts` pins both against `fleet.yaml`.

Whether a slot is *required* is the server's field, not `kind`'s: the route permit is required for
Mode A and optional for Mode B, so one slot answers differently on two vehicles.
`canBeApproved(slots)` is US-27.3's rule as one predicate, and an empty slot list is `false` — a
vehicle nobody has read the paperwork of has satisfied nothing.

### A document is attached to a vehicle, so `?vehicle=` is the screen's state

The wireframe draws the four slots inside the add-vehicle card, which is the one place a vehicle
does not exist yet. `POST …/vehicles/{vehicleId}/documents` needs one, so the panel renders for
`?vehicle={id}`: the add form navigates there on success, every roster row links to it, and the
slots are server-rendered from that vehicle's own `GET …/documents`.

### The Paid gate is read for an Owner and refused for a Manager

`canSetPaidServicePayment` and `PAID_SERVICE_PAYMENT_BLOCKED_KEY` come from C112's
`src/api/payout.ts` — one predicate, one sentence. But `GET …/payout-profile` is
`RequireFleetSubRole(Owner)` and SCR-FP-004 is Manager-reachable, so **the profile is read only for
an Owner**: an Owner gets "Paid" disabled with that sentence before the press, a Manager gets it
enabled and fleet-svc's `409 payout-profile-not-verified` translated to the same sentence after.
Both are blocked; only one can be told in advance.

### Three services answer SCR-FP-006, and their gates disagree

`POST …/trackers/bind` is fleet-svc's and **is** approval-gated; `POST …/trackers/bulk` is
provisioning-svc's and is **not** (it is gated on the canonical `fleet_owner` role alone);
`GET …/health` is fleet-health-svc's. Each is transcribed where it is, because guessing high would
refuse a write the platform allows. The screen therefore opens for a PENDING organisation with the
bind form replaced by a sentence and the batch still available.

The batch is also the **one control on this portal the gateway can refuse for being a browser**:
`bulkBindTrackers` carries `X-Attestation`, so `AttestationMiddleware` lists it as a D-30 sensitive
operation and a request with no `X-Platform` is `401 attestation-failed`. It is `Disabled` outside
production, the control is drawn, and the refusal has a sentence of its own
(`fleet.error.attestationFailed`). See the C113 handoff.

### What SCR-FP-005 and SCR-FP-006 cannot do, and say so

Two more affordances the wireframe draws have **no route on any contract**:

1. **Inviting a driver.** SCR-FP-005 sketches an "Invite sent · Resend" row. `POST …/assignments`
   answers `404 driver-not-found` for a number with no Driver App account, and no fleet-driver
   invitation template exists. The screen says a driver signs up in the Driver App first.
2. **A per-vehicle publish-cadence profile** (US-3.18). The only cadence surface on the platform is
   the MQTT downlink `veh/{vehicleId}/cmd`, which is a device topic. The column reports US-5.5's
   standing rates (`PUBLISH_CADENCE`) and the caption says the profile cannot be set from here.

### Two more rules for a screen component

- **`Date.now()` never decides a server fact.** `Assignment.active` is "the validity window
  evaluated by the database at read time" and US-13.9's auto-expiry is that flag going false with
  nothing written. The portal reads it; the clock is only used to *label* a row the server already
  called inactive.
- **Money crosses the boundary once.** `fareMinorFrom()` in `src/api/vehicles.ts` is the single
  rupees→cents conversion; everything on the wire is integer minor units.

## What C114 is — SCR-FP-003, SCR-FP-007 and SCR-FP-009

Situational awareness: the **dashboard**, the org-scoped **live map** and the **trip/analytics
report**. `app/(portal)/{dashboard,map,analytics}` + `app/(portal)/analytics/export/route.ts`,
`src/api/{insights,billing}.ts`, `src/components/{dashboard,map,analytics}/` and
`src/components/KpiTiles.tsx`. It added no nav entry — C111 declared all three — and one shell
behaviour: `canReadBilling()`/`billingRefusal()` in `src/server/access.ts`.

### The map is the portal's only browser-side library, and its only browser-side URL

`maplibre-gl` (+ `pmtiles`) is the one runtime dependency beyond React and Next. D2 §FP names it:
"Single org-scoped MapLibre map (row-level security), fleet-health overlay".

- **It is handed positions and fetches none.** `GET …/map` is read on the server and passed down;
  the browser holds no bearer and cannot reach the gateway (`src/api/http.ts` is `server-only`), so
  "only this org's vehicles are visible" is not a filter the component applies — it is the only
  data it ever receives. The database refuses underneath (`telemetry.positions_fleet`, filtered on
  `app.fleet_id`, fail-closed).
- **`FLEET_PORTAL_MAP_STYLE_URL` is the one URL a browser fetches**, and deliberately not the
  platform: D-14's `tile-cdn` is static cartography on a CDN. It is passed as a **prop**, not
  published as a build-time public variable, so the shell's "the browser never sees the platform"
  rule is intact. Unset is supported — the fleet's own positions render on an empty canvas and the
  screen says so, because a missing basemap must not read as missing vehicles.
- **Markers are a GeoJSON source and two circle layers, not `Marker` DOM nodes.** A `<div>` per
  vehicle would need an inline `style` for its colour, which AL-52 forbids and `test/fences.test.ts`
  fails the build on. The hexes come from `@mageride/tailwind-preset`'s token data, which exists
  for exactly this; with no basemap MapLibre clears the canvas transparent, so the container's
  `bg-surface-variant` carries light/dark and no JavaScript reads a theme.
- **`MapOptions.locale` replaces the library's English UI strings**, and the page keys the
  component on the locale so a language switch rebuilds it. MapLibre takes them once.
- `maplibre-gl/dist/maplibre-gl.css` is imported. It is a widget's functional stylesheet compiled
  at build time by the same PostCSS pipeline — not a pre-styled kit, not runtime CSS-in-JS — and
  the OSM attribution control it styles is a licence requirement, not a decoration.

### Selecting a vehicle is a URL

`?vehicle={id}` — pushed by a marker click, followed from an overlay row, pasted from a message.
The drill-in panel is server-rendered from data the page already has, so it cannot disagree with
the table beside it, and an id from another organisation resolves to "not in this organisation"
rather than to a marker.

### Two windows, and the overlay is built over the union

fleet-svc drops a position older than `Fleet:MapStaleAfter` (15 min) from the map answer;
fleet-health-svc calls a tracker `offline` after `Health:OfflineAfter` (30 min). **They do not line
up and are not meant to** — one is a stale coordinate, the other a silent device. So the overlay is
the union of both reads: a vehicle can be Offline in the table with no pin on the map, and the
caption states both windows in the deployment's own numbers.

### Idle is a subtraction, and the screen says what it therefore means

`VehicleAnalytics` has no idle field. fleet-svc defines `utilisationPct` as
`activeHours × 100 / periodHours`, so idle is that definition's complement over the same period —
the server's own arithmetic, not a second measurement. It is **calendar** time: nothing measures a
stationary running engine, so an overnight park is idle, and the caption says so. Two more captions
carry what the report cannot claim: the kilometres are great-circle hops between samples (not road
distance, C059 handoff), and there is **no earnings column** because `earningsMinor` is returned
absent on purpose (BR-23.10 — a fleet's fares never reach MageRide).

### The CSV is written here; the PDF is the browser's

`web_fleet.html` draws "Export CSV / PDF" and **no contract has an analytics export route**
(`exportFleetInvoice` is fleet-billing-svc's and is an invoice). So `app/(portal)/analytics/export/route.ts`
re-reads the same org-scoped `GET …/analytics` with the same `from`/`to` and writes the rows —
no figure that is not on the page. Its path sits under `/analytics`, so `resolveScreenRoute` claims
it for SCR-FP-009 and `proxy.ts` gates it as that screen. The PDF is `window.print()` over
`print:hidden` chrome, which is why `PortalChrome`'s rail and topbar carry that class.

### Billing is the one **read** on this portal gated on the seat

`FleetBillingAccessFilter` gates every route in `fleet-billing.yaml` — reads included — on the
Owner sub-role **and** an APPROVED organisation, which is stricter than fleet-svc and stricter than
URD §2.3 alone. `canReadBilling()` is that gate on this side and is checked *before* the wallet
card reads anything: a Manager's dashboard is not a Manager's dashboard with three 403s on it.
`billingRefusal()` separates "not the Owner" from "still in verification" because an operator does
two different things about them. `src/api/billing.ts` carries only the two reads the dashboard
card makes — **C115 owns SCR-FP-010** and the invoice detail, export, receipt, Pay verb and the
three top-up routes belong in that file when it lands. The card's own "Top up wallet" is a link.

### What SCR-FP-003 cannot do, and says so

1. **Insurance expiring (30 d).** Expiry dates live on a vehicle's own document slots
   (`GET …/vehicles/{id}/documents`) — one request per vehicle — and no fleet-wide document-expiry
   route exists. The card carries "vehicles with documents outstanding", which one roster read does
   answer, and a caption saying where expiry is shown instead.
2. **A projected next invoice.** Nothing publishes the per-vehicle monthly rate or forecasts a
   month that has not been run, so the card names the oldest **open** invoice (`OVERDUE` before
   `DUE`) with `wallet.outstandingMinor` beside it, and says when the next one is raised otherwise.
3. **Route-deviation and geofence alerts.** US-13.5 is Phase 3 with no producer; `GET …/alerts`
   answers an empty page "so the Fleet Portal can render an empty state without a later breaking
   change". The card renders that state as a sentence. **No alerting is built here** — every other
   row is a count of rows a service already decided about (a `MISSED` schedule, an offline tracker,
   US-3.16's device-down window).

### One more rule for a screen component

- **A screen renders no `async` child component.** `ProblemPanel` is one and only Next can render
  it, which is why the dashboard's wallet reads happen in the page and the card is synchronous —
  a page whose tree contains an unresolved async child renders as nothing under `@testing-library`,
  and that is a test that cannot be written rather than a test that fails.

## What C115 is — SCR-FP-008 and SCR-FP-010

The two screens the Manage group is named for: **scheduling with US-13.11's not-started alarm**, and
the **consolidated monthly invoice with the fleet wallet it is settled from**.
`app/(portal)/{scheduling,billing}` + `app/(portal)/billing/export/route.ts`, `src/api/schedules.ts`,
`src/api/billing.ts` (completed), `src/server/{schedule,billing}-actions.ts` and
`src/components/{scheduling,billing}/`. It added no nav entry — C111 declared both — and one shell
behaviour: `download()` in `src/api/client.ts`.

### The departure clock is Colombo's, and that is the one bug this screen could not survive

`<input type="datetime-local">` has no time zone on it, and the server action that reads it runs in a
container set to **UTC**. `new Date('2026-06-18T06:00')` there is 11:30 in Colombo — a bus booked out
five and a half hours late with an alarm to match. `departAtFrom()` resolves the wall clock against
`Asia/Colombo` explicitly (D-13), reading the offset out of `Intl`'s own zone rules rather than
writing `+05:30` down, and `colomboLocalNow()` writes the form's `min` in the same clock.

### Whose app rings is worked out the way the alarm worker works it out

`ScheduleAlarmWorker` resolves the recipient from the assignment covering the **booked departure**,
not the one covering now — "an alarm raised at 06:20 about the 06:10 belongs to the 06:10's driver".
`driversCovering()` is `DriversCoveringAsync`'s predicate transcribed, vehicle included, and the
Vehicle cell says who would be rung — or that **nobody is assigned over this departure**, which is
the case the worker otherwise discovers at alarm time and logs as "there is nobody to tell".

That is not the portal re-deriving `Assignment.active`, which stays the database's (C113's rule): it
is window arithmetic over an instant that has nothing to do with the clock.

### What SCR-FP-008 cannot do, and says so

1. **Change or cancel a booked departure.** `fleet.yaml` declares exactly two operations on
   `/schedules` — list and create. `CANCELLED` is a status 0314's CHECK admits and attributes to
   "the operator", and **nothing on the platform can write it**. The deliverable asks for
   "add/change", so this is a gap; `test/schedules.test.ts` parses the contract's own path block and
   fails if a third verb appears.
2. **Turn the alarm off.** `not_started_alarm_minutes` is `NOT NULL DEFAULT 10` with a 1…120 CHECK,
   so the wireframe's toggle has no state to write. What an operator chooses is the offset, once.
3. **Name or choose a route.** `FleetSchedule.routeId` is a `spatial.routes` id (FK added by 1408)
   and no contract lists those rows; `GET /v1/transit/routes/{routeId}` is transit-svc's and takes a
   **GTFS string**, an entirely different id space. The column shows the reference; the form sends
   none.

### SCR-FP-010 reads nothing until the caller is known to be entitled

`canReadBilling()` is checked **before the first read**, not around the results: a Manager who
follows a link gets one sentence naming whose screen it is, rather than four 403s and an audit trail
of an access attempt nobody made. The nav does not offer the entry and `proxy.ts` refuses the route;
this is the third of the three, and it is the one that stops the requests.

### The Mode A row is on the card and is not on the invoice

The sketch draws "Mode A vehicles · 88 · Free · Rs 0" and **no invoice has a Mode A line** — a line
exists only for a charge `billing.monthly_subscriptions` raised, and that table carries Mode B rows
only (AL-03). So that count is `GET …/vehicles` **today**, contributes nothing to the total, and the
caption says which of the two it is. `invoiceSummary()` totals Σ of the lines and compares it against
both `lineSumMinor` and `invoice.amountMinor` — the contract returns the first "so a client can check
rather than trust" — and the card draws a warning rather than picking one when they disagree.

### The invoice document is fleet-billing-svc's; the analytics CSV was ours

`/billing/export` streams `GET …/billing/{invoiceId}/export?format=csv|pdf` through `download()`,
because the platform renders both and its CSV "prints money twice — rupees for a bank reconciliation,
integer minor units for a reconciliation against this platform". SCR-FP-009's CSV is written in this
repo for the opposite reason: **no contract has an analytics export route at all.** The difference is
not a preference.

`download()` exists because the browser holds no bearer and cannot reach the gateway, so a link
straight to the API would download a `401`. `apiFetch` gained `binary` + `accept` for it; a failure
is still `application/problem+json` and still a `ProblemError`.

### Nothing on the top-up form credits anything

`POST …/wallet/topup` opens a **payment session**; the wallet is credited on the provider callback
(D-09), "never here: a session the gateway accepted has moved no money". So the form ends in a link
to finish paying and a **Check payment** button — a button, not a timer, because the operator is
completing the payment on a bank app or a hosted card page, often on another device.

Three more things about the rails:

- **The sketch's three rows are two rails.** `method` admits `onepay` and `lankaqr`; **OnePay is the
  card rail** (`Onepay:ApiKey` unset ⇒ "the card rail answers 503"), so a card is entered on OnePay's
  hosted page rather than being a third method. Bank transfer is not offered anywhere (AL-05) and
  `ck_fleet_topups_method` is where that is actually enforced.
- **No `returnUrl` is sent.** It is passed straight to OnePay, this portal has no configured public
  origin, and deriving one from the request's `Host` would hand a payment gateway a caller-controlled
  value.
- **The LankaQR payload is shown as text, not as a code.** An EMVCo payload belongs to the acquiring
  bank and arrives whole; rendering it would put a QR encoder in the browser bundle for the fallback
  AL-15's deep link exists to avoid.

### The Pay button is drawn for the two states that can be paid

`DUE` and `OVERDUE`. `FREE` and `PAID` both answer `409 invoice-not-payable` and both are knowable
before the press. `402 insufficient-wallet` is **not** one of them — that is an amount to top up, the
invoice is deliberately left open, and the operator has to be able to try. Settlement is idempotent
by construction (`fleet_invoice:{invoiceId}` is UNIQUE in `billing.journal_entries`), so the
`Idempotency-Key` is a fresh one rather than a second, weaker guard over the same money.

## Configuration

`.env.example` documents every variable. `MAGERIDE_API_BASE_URL` (the C008 gateway origin) is
required; absent, every request answers 503 rather than 500. The four OIDC variables are optional
in pairs — unset, that provider's button is not rendered at all rather than rendered and broken.
`FLEET_PORTAL_MAP_STYLE_URL` (Δ C114) is optional — see the map section above.

`output: 'standalone'`, so the container entrypoint is `node .next/standalone/portals/fleet/server.js`,
not `next start`.
