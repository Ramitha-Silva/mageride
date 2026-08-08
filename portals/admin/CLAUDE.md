# Admin Portal (C104 shell) — `admin.mageride.lk`

Next.js 16 (App Router) + TypeScript + React 19, styled **only** with Tailwind through
`@mageride/tailwind-preset` (AL-52). npm workspace member `@mageride/admin-portal` under `portals/`.

**Verify:** `npm --prefix portals run lint --workspace admin && npm --prefix portals run test --workspace admin && npm --prefix portals run build --workspace admin`

`backend/contracts/admin-bff.yaml` and `backend/contracts/iam.yaml` are normative for the wire
shapes and win over this file and over the code.

## What C104 is

The **application shell**: session, role-scoped navigation, the data layer to admin-bff, and the
page chrome the nineteen screens sit inside. C104 owns no wireframe screen ID except
**SCR-AP-001 (sign-in)**, which is session handling rather than a module — the queues, the
directories, the finance screens and the configuration screens belong to C105…C110, which drop
their own `app/(portal)/…/page.tsx` beside the catch-all placeholder.

Shared infrastructure (C103) — use it, do not re-implement it: `@mageride/tailwind-preset`
(D2 §0.2 tokens), `@mageride/ui` (button/field/chip/pill/table/modal/toast/tabs/dropzone),
`@mageride/eslint-config` (`react` flat config). `@mageride/i18n` carries what **every** surface
shares; this portal's own copy lives in `src/i18n/messages/{en,si,ta}.ts`.

## The five load-bearing decisions

### 1. The RBAC gate is `proxy.ts`, and it is not in a layout

An App Router **layout is reused, not re-rendered, when navigation moves between its children**.
A guard in `app/(portal)/layout.tsx` would run on the first page load of a session and never
again — which is exactly the case a route guard exists for. `proxy.ts` (Next 16's `middleware.ts`;
Node runtime, so `process.env` is read at run time and not baked into the image) runs on every
request including the RSC fetch a client-side navigation makes.

It refuses by **rewriting** to `/denied`, whose page calls `forbidden()`. That is what makes the
status a real **403** with the operator still on the URL they asked for; a page that merely said
"no" with a 200 would be invisible to every check that matters.

### 2. The portal never decides who may see what

`GET /v1/admin/session` returns the menu admin-bff already filtered through the same
`IPermissionEvaluator` its endpoints gate on. **A screen is reachable iff its nav item is in that
menu** — there is no `if (role === …)` anywhere in this application and there must never be one.
URD §2.3 exists in the URD and in `MageRide.Shared.Auth.PermissionMatrix`; a third copy here would
be the one nobody's test parses the spec to check.

`src/server/routes.ts` is the *one* local copy, and it carries only key → path. It exists so a
nested screen (`/verification/expiring`) is checked on **its own** gate instead of inheriting its
parent's — deny-by-default over path prefixes alone leaks the child to anyone holding the parent.
`test/routes.test.ts` parses `AdminMenu.cs` and fails if the two drift.

**It is still not authorization** (AL-06/US-21.1): every endpoint re-decides. What this stops is a
console offering a screen whose every request would be refused.

### 3. No MFA, and no way to add one by accident (AL-37)

There is no challenge screen, no second factor, no branch on `mfaRequired` — which is typed
`false` so no code path for the true case can be written. Sign-in is iam-svc's
`POST /v1/admin/auth/login`; **no credential is checked here and none ever will be**, because a
second place a password is checked is a second place the failed-attempt lock-out is forgotten.
`test/fences.test.ts` asserts the absence against the tree.

The lock-out **is** surfaced: `423 otp-locked` carries `retryAfterSeconds`, and the form says "try
again in about N minutes" rather than leaving an operator to guess — every guess extends it.

### 4. The browser never holds a token and never sees the platform

Every call leaves the Next server. The session lives in httpOnly cookies; `src/api/http.ts` is the
only module that calls `fetch`, and it is `server-only` so a client component importing it fails to
compile. There is no `NEXT_PUBLIC_*` variable and there must not be one.

### 5. Mutations declare their D-35 row

`mutate()` requires an `AuditIntent` — the `audit.events` action and entity the call will cause.
The portal does **not** write the row (admin-bff's interceptor does, inside the same transaction as
the change); it declares it so a confirm dialog can tell the operator which row their name is about
to appear on, and so `AuditNotice` cannot be rendered for a call whose row was never named.
Every mutation carries an `Idempotency-Key`.

**Δ C108 — or an `AuditedElsewhere`.** Four `/v1/admin/**` prefixes are routed past admin-bff by the
gateway and write no row at all, so `audit` is a union and the second arm names the service that
answered instead. It is still **required**: "this screen forgot to declare its row" and "this route
writes none" must not be the same value. See the C108 section.

## Rules for a screen component (C105…C110)

- **Add `app/(portal)/<path>/page.tsx`** at the path `AdminMenu.cs` gives your nav item. It takes
  precedence over the catch-all automatically; nothing else needs registering. If the manifest gains
  an item, add it to `src/server/routes.ts` in the same change or `test/routes.test.ts` fails.
- **Call `read()` / `mutate()` from `@/api/client`.** `test/fences.test.ts` enumerates the tree and
  fails on a raw `fetch`, on `apiFetch` outside the three modules that own it, and on any `/v1/**`
  path outside the four the shell needs.
- **Every string goes through the translator, in all three files, in the same change.** Add screen
  copy to `src/i18n/messages/{en,si,ta}.ts` — `en.ts` defines the key set and the other two are
  typed against it, so a missing translation is a compile error. The lint rule stops a literal
  reaching JSX.
- **Render a failure with `<ProblemPanel>`.** Never `problem.title` — `_shared.yaml` says it in as
  many words: "Short English summary for developers. Never localised."
- **`sm:` is 375px, `md:` 768px, `lg:` 1024px, and there is no `xl:`.** The preset replaces
  Tailwind's breakpoints; D2 §AP defines three widths and the portal gets three.
- **Dark mode is the `.dark` class on `<html>`, set once in `app/layout.tsx`.** Do not add a second
  mechanism; the tokens and the `dark:` variant are wired to the same class and must stay that way.
- **`@mageride/ui` is importable from a server component.** `Field`/`Input`/`Select`/`Textarea`,
  `Tabs`, `Modal`, `Toast` and `Dropzone` carry `'use client'` and become a client boundary; the
  rest (`Button`, `Table`, `StatusPill`, `Chip`) render on the server. **Δ C105:** `Field` shipped
  without the directive, and because `index.ts` is a barrel that made the *package* client-only —
  a server component importing `Table` pulled `createContext` into the server graph and the build
  failed. `portals/ui/test/server-components.test.ts` is now the executable form of that rule.
- **Reach for a client component when something is interactive, not when something is a form.**
  A `<form method="get">` and a `<Link>` need no JavaScript, and a filter held in the URL survives a
  reload, a bookmark, the back button and a link pasted into a ticket (`StatsFilter`).

## SCR-AP-002 — the dashboard and its statistics filter (C105)

The first screen to land beside the shell, and the template for C106…C110.

- **The filter is the URL and nothing else.** `?period=today|week|month|custom&from&to`, four
  `<Link>`s and a `method="get"` form — no client state, so a comparison survives a reload and can be
  pasted into a ticket. `src/api/dashboard.ts` owns the one function that builds that query, and the
  page and the export both call it: "the CSV contains exactly the filtered figures on screen" is one
  query into a service that renders both from one call, not two implementations that agree.
- **Period KPIs recompute; the three live cards do not.** They arrive on the same payload and mean
  different things (AL-38, D6' §I-28.5), so they are drawn under separate headings — a filter that
  visibly moved five figures and not three would otherwise read as broken.
- **A half-chosen custom range asks admin-bff nothing.** `StatsSelection.awaitingRange`. Substituting
  today's figures would put the wrong number under the right heading — the substitution C061 refuses
  to make server-side — and sending the incomplete query would answer the operator's first click with
  a validation error about a form they have not filled in.
- **An absent delta is `—`, never `0 %`.** `null` means the previous period was empty and there is no
  percentage; zero means a comparison that found no change. The glyph and the figure are
  `aria-hidden` and a full sentence naming the metric is `sr-only` beside them.
- **The export is a route handler under `/dashboard`,** so `resolveRoute` gates it on the same nav
  item as the page — no entry in `routes.ts`, no exemption. It relays bytes; it does not render a
  second CSV. `apiDownload`/`download` are the file-shaped members of the data layer and are named in
  `test/fences.test.ts` alongside `apiFetch`.
- **The alerts feed links a row only when the caller's menu carries that module.** The count reaches
  every permitted role; the queue does not. The link comes from the item admin-bff sent, not from
  `routes.ts` — the server's own path is the one its own gate agrees with.

## SCR-AP-003/003a/003b/003c — the Verification Officer's screens (C106)

Four screens and one route handler under `/verification`, all reading C063's AL-39 family.

- **A queue is not filtered here; a queue *is* the filter.** Membership is "a `registry.document_fields`
  row is still `pending`" — AL-27 as the query it is, decided in admin-bff — so an auto-verified
  document cannot appear rather than being filtered out by code that could later stop filtering.
  The status column is the **subject's own** registration status, which is what D2's status filter
  filters on. Nothing in this portal re-decides membership. Δ the wireframe draws an `Auto-verified ·
  View` row in the driving-licence queue; no such row can exist. See the C106 handoff.
- **All three queues are read on every render**, with one search and one status filter. The wireframe
  puts a count on each tab and their sum in the topbar, and cursor pagination carries no total — so
  the badge is the number of rows a queue answered, `100+` past a page, and `—` for a queue that
  failed. A queue that fails does not take the screen with it.
- **The tabs are links, not `@mageride/ui`'s `Tabs`.** That primitive holds the active tab in state,
  and an officer who opens a row, decides, and comes back would come back to the first tab. The tab,
  the search and the status travel on every link the four screens draw (`components/verification/links.ts`).
- **Every document fetch goes through `/verification/media/{docId}`.** `DocumentRef.thumbUrl` /
  `fullUrl` are deliberately unused: the browser holds no bearer, so the relay makes the call, and it
  is built from `docId` so no upstream string reaches an `src`. admin-bff records `DOC_VIEW` and
  *then* mints the signed object-storage URL it `302`s to; this handler passes that redirect on
  rather than following it, so the bytes never enter this process. **One view is one row** — rendering
  a grid of six thumbnails is six rows, which is why they are not lazy-loaded and the response is
  `no-store`.
- **Approve is disabled while any flagged field is unconfirmed** (US-2.10a), and the rule is stated
  three times on purpose: on the button, in `decideSubject` (a disabled button describes a page that
  may be a minute old), and in admin-bff, which answers `409` and is the only one that is
  authorization.
- **Confirm sends no `value`; Edit & confirm sends the officer's.** One route, one optional field, and
  that difference is what decides whether the extraction stays evidence or the field becomes
  `manual` with no confidence. A value typed into the box and then abandoned is ignored by Confirm.
- **`/verification/expiring` is a different screen.** A single dynamic segment out-ranks the shell's
  catch-all, so `[subjectId]/page.tsx` is the file Next renders for it; it hands any path that
  resolves to another nav item back to `<ScreenPlaceholder>` (extracted from the catch-all for this).
  Ids are checked against the `{subjectId:guid}` shape admin-bff routes on before they reach a path
  this process builds.
- **The copy deviation:** the Approve button is `Approve driver` / `Approve vehicle` / `Approve
  organisation` rather than the wireframe's "Confirm all & approve" — it confirms nothing, and it sits
  disabled for exactly the reason that label would have promised to fix. SCR-AP-003c's own button is
  the wording followed.

## SCR-AP-004/005 — moderation and the support queue (C107)

Two screens, at `/reports` and `/support/tickets` — the paths `AdminMenu.cs` gives their nav items;
the wireframe's `/moderation` is the *group*, whose third member is C065's fraud queue.

- **A queue is not filtered here either.** The report queue is "a `safety.vehicle_reports` row is
  still `PENDING`", decided in safety-svc and forwarded, so a decided report leaves because it is no
  longer pending. There is no status control on SCR-AP-004 and there must not be one.
- **A pending report is not a strike.** Three *confirmed* reports delist a vehicle (US-12.6) and
  `ReportRow.confirmedCount` is `null` on every row admin-bff answers with — so the queue's count
  column says "{n} pending" and means it, and the confirmed total appears **only** on the banner
  after a verdict, which is the one moment the platform states one. Printing the pending count as a
  strike total would tell a moderator they were one press from delisting somebody when they are
  three.
- **A suspension has no duration and the wireframe's dropdown is not drawn.** `ReasonBody` is one
  field, admin-bff writes `dispatch_state`/`is_blocked`, and nothing reinstates. The card says so.
  "Driver / vehicle ID" is two controls, because they are two routes doing two different things.
- **The ticket being read is `?ticket=`, not a path segment** — admin-bff exposes no
  `GET /v1/admin/support/tickets/{ticketId}`, so the detail is a row out of the page the queue was
  read from (`findTicket`). D2 gives the queue and the ticket one screen id, so one screen with two
  panes is also what the wireframe draws.
- **Resolve is the only decision.** support-svc's `…/respond` (reply without closing) has no
  admin-bff route, so the wireframe's Reply button is absent rather than dead.
- **The refund hand-off is a link and posts nothing.** URD §2.3's Refunds row gives the CSR
  `◐ raise/recommend` — Read opens SCR-AP-006's queue, Write is withheld — and a `daily_fee_refund`
  or `driver_qr_dispute` ticket is *already* on Finance's pile, because support-svc derives the queue
  from the category and never stores it. `FINANCE_CATEGORIES` mirrors `TicketQueues.FinanceCategories`
  and `test/support-model.test.ts` parses the C# to hold the two together.
- **The directory lookups come from the caller's menu, both of them.** `TicketRow` carries a `userId`
  and nothing saying which directory holds it, so passenger and driver are offered and the path is
  the one `GET /v1/admin/session` sent (`AlertsCard`'s rule).
- **`CursorPage` now lives in `src/api/types.ts`** (Δ C107) and `src/api/verification.ts` re-exports
  it. Three screen groups page; the envelope belongs to none of them.

## SCR-AP-006/007/008/009 — finance, configuration, RBAC and the trail (C108)

Fourteen routes across four wireframe screens, and the component where **the platform's own gaps
are the design**. Read `AuditIntent` first: it changed shape here.

- **`mutate()`'s `audit` is now a union, and the second arm is a confession.** `gateway-routes.json`
  matches `/v1/admin/fees/**`, `/v1/admin/voucher-discount-tiers`, `/v1/admin/drivers/level-config`
  and `/v1/admin/rbac/**` at **Order 20**, ahead of admin-bff's Order 90 catch-all — and admin-bff
  maps no route onto any of them (the GTFS proxy is the shape that *does* work: shadowed in
  production, present so "the RBAC matrix and the audit guard cover the path either way"). So those
  four writes reach their owning service and **leave nothing in `audit.events`**, against D-35 and
  against US-21.14. They declare `{ auditedElsewhere: 'subscription-svc' | 'dispatch-svc' |
  'iam-svc' }`, `AuditNotice` says which service answered instead, and the C108 handoff raises it.
  Declaring it beats omitting it: "this screen forgot its row" and "this route writes none" must not
  be the same value.
- **Three configuration surfaces have a write and no read.** Fare tariffs, daily-fee rates and the
  Driver-Level parameters are `PUT`-only across every contract on the platform. The forms therefore
  **start empty and say so**; seeding them with D2's illustrative figures would invent the platform's
  live prices, and the first operator to trust that publishes a version over the top of what is
  really running. The voucher ladder and the feature flags do have reads and are read.
- **One D2 screen, four nav items, one tab strip built from the caller's menu.** SCR-AP-006 is drawn
  as five tabs; `AdminMenu.cs` splits it because they are four different URD §2.3 rows. `financeTabs`
  / `configTabs` resolve each tab against the item admin-bff sent, so a Support CSR sees one tab and
  a Verification Officer sees none — `AlertsCard`'s rule, applied to navigation.
- **`holdsGrant(session, area, grant)` gates a *control* where a nav item is too coarse.** The refund
  queue is one screen with two audiences (`◐ raise/recommend` vs `✅ approve/execute`), so the raise
  form is drawn from the caller's own `permissions`, which is admin-bff's evaluation read back rather
  than a second copy of the matrix. It cannot make admin-bff's precise `RequiresOwnScope(needed)`
  check — the session response collapses `ScopedGrants` to a boolean — so `test/finance-access.test.ts`
  parses URD §2.3 and fails the build if any internal role ever holds a scope-limited write in a
  gated row.
- **SCR-AP-008 is a lookup, not a directory.** iam-svc has no route that lists internal users, none
  that provisions one, and none that suspends an account or its sessions. Those wireframe
  affordances are absent rather than dead, and the screen says why in the operator's own language.
  The permission-set toggles are **cells**: `getPermissionMatrix` is read-only by design, because a
  Super Admin who could edit the matrix could grant themselves something URD §2.3 forbids.
- **The audit export is the one export this portal renders itself.** Every other one relays bytes a
  service produced; `/v1/admin/audit-log` has no `.csv` sibling and US-19.3 asks for one. It follows
  the cursor to `AUDIT_EXPORT_MAX_PAGES` and states the cap on the screen, in the `#` preamble and
  again in the file when it actually bit — a silent truncation is the one failure an audit export
  cannot have. The handoff asks admin-bff for the route that would let it be deleted.
- **Money is formatted to the cent here, not to the rupee.** `formatMoneyMinor` /
  `formatSignedMoneyMinor` sit beside `formatMinorUnits` because a two-cent reconciliation variance
  is still a variance, and the KPI card's rounding would print it as `0` under a pill saying the
  rails disagree.
- **Payouts is a tab, not a card.** `payout.yaml` names SCR-AP-006 and `Payout.Api` is built, but the
  gateway has **no payout-svc cluster**, so both reads 404 today. Behind a tab, the failure appears
  when somebody asks for payouts instead of greeting every Finance Officer with an error panel over
  a working reconciliation.

## Configuration

`.env.example` documents every variable. `MAGERIDE_API_BASE_URL` (the C008 gateway origin) is
required; absent, every request answers 503 rather than 500. `GOOGLE_OIDC_CLIENT_ID` /
`GOOGLE_OIDC_REDIRECT_URI` are optional — unset, the Google button is not rendered at all rather
than rendered and broken — and the redirect URI must equal iam-svc's `Oidc__Google__RedirectUri`
and be registered on the Google client.

`output: 'standalone'`, so the container entrypoint is `node .next/standalone/server.js`, not
`next start`.
