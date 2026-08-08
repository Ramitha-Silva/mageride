# Fleet Portal (C111 shell + C112 auth/org/payout + C113 vehicles/drivers/trackers) — `fleet.mageride.lk`

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
- **Call `read()` / `mutate()` from `@/api/client`, with `{ org: … }`.** `test/fences.test.ts` fails
  on a raw `fetch`, on `apiFetch` outside the three modules that own it, on a `/v1/**` path outside
  the seven the shell needs, and on any second place that builds a `/v1/fleets/{id}` URL.
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

## Configuration

`.env.example` documents every variable. `MAGERIDE_API_BASE_URL` (the C008 gateway origin) is
required; absent, every request answers 503 rather than 500. The four OIDC variables are optional
in pairs — unset, that provider's button is not rendered at all rather than rendered and broken.

`output: 'standalone'`, so the container entrypoint is `node .next/standalone/portals/fleet/server.js`,
not `next start`.
