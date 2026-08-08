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

## Configuration

`.env.example` documents every variable. `MAGERIDE_API_BASE_URL` (the C008 gateway origin) is
required; absent, every request answers 503 rather than 500. `GOOGLE_OIDC_CLIENT_ID` /
`GOOGLE_OIDC_REDIRECT_URI` are optional — unset, the Google button is not rendered at all rather
than rendered and broken — and the redirect URI must equal iam-svc's `Oidc__Google__RedirectUri`
and be registered on the Google client.

`output: 'standalone'`, so the container entrypoint is `node .next/standalone/server.js`, not
`next start`.
