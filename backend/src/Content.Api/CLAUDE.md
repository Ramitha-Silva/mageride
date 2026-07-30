# content-svc (C045) — server-side i18n and public reference data

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis. References `MageRide.Shared`
(C002). **No Kafka and no outbox**, and a command log for exactly one route — see the rules below.

**Verify:** `dotnet test backend/src/Content.Api.Tests -c Release`

`backend/contracts/content.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is

Four datasets and one rule. The rule is CLAUDE.md's: **every user-facing string exists in Sinhala,
Tamil and English, or it is not publishable** (D-26).

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/config/cities` | **none** | D3' content-svc public reference data, AL-27, D4' §17b |
| `GET /v1/content/onboarding/{audience}` | **none** | **Δ C045** — AL-28, BR-25.1, US-1.2/1.2a |
| `GET /v1/content/templates/{key}?lang=` | internal | D3' content-svc, D-26, D6' I-29.2 |
| `GET /v1/content/faq?lang=&category=` | Bearer | **Δ C045** — the deliverable's own wording, US-16.1 |
| `GET /v1/content/broadcasts?lang=` | Bearer | D3' content-svc, US-14.8 |
| `GET /v1/admin/content/{key}` | admin | **Δ C045** — the approval workflow needs a read |
| `PUT /v1/admin/content/{key}` | admin | D3' content-svc, D-35 |
| `POST /v1/admin/content/{key}/approve` | admin | **Δ C045** — D3' calls the edit an approval *workflow* |
| `POST /v1/admin/content/broadcasts` | admin | **Δ C045** — US-14.8 had a reader and no writer |
| `POST /v1/internal/content/cache/purge` | internal | **Δ C045** — the one dataset admin-bff writes |

| Table | Read | Written |
|---|---|---|
| `content.notification_templates` | render path | admin publish + approve |
| `content.faq_articles` | `/v1/content/faq` | **nobody** — migration 1902 is the day-0 set |
| `content.broadcasts` | `/v1/content/broadcasts` | admin publish |
| `content.onboarding_slides` | the carousel | **nobody** — migration 1903 |
| `config.operating_cities` | `/v1/config/cities` | **admin-bff** (C065), not this service |
| `content.command_log` | the idempotency middleware | the same, on every POST that carries a key |

## The two fences, and how each is held structurally

- **A template, broadcast or slide missing a language is invalid.** Held three times over, on
  purpose. `TrilingualText` **cannot be constructed** with two languages, so no code path here can
  write one. Migration 1307's `trg_notification_templates_trilingual` is a `DEFERRABLE INITIALLY
  DEFERRED` constraint trigger that counts languages per `(template_key, version)` at COMMIT, and
  1307's `ck_onboarding_slides_*_trilingual` and `ck_broadcasts_trilingual_strict` do the same for the
  other two tables — because this service is not the only thing that can write them. Those two use
  `content.is_trilingual_text`, not C005's `?&`: `?&` tests **key presence**, so
  `{"si":null,"ta":"","en":"ok"}` satisfies it and what ships is a blank message in one language.
  (C005's original constraint stays; the strict one is added beside it, `NOT VALID` on `broadcasts`
  because that column predates this script.) The service-side rejection exists on top of all of them
  because the definition of done asks for a *clear* error: a constraint violation is a 500,
  `bodyByLang.ta` with a reason is a form field an author can fix.
- **Only active operating cities are served publicly.** `WHERE is_active` is inside the query
  (`ReferenceDataRepository`), never a filter over its result. D4' §17b makes toggling `is_active` how
  an admin takes a city out of the apps with no release, and a passenger who could pick a city the
  platform does not operate in gets an empty map and a number nobody answers.

## Rules that are load-bearing

- **The cache is in process; only the purge crosses replicas.** Every dataset here is tiny (three
  cities, six slides, twelve FAQ rows, a handful of template keys) and the template read is on the
  hottest cold path the platform has — E-01 renders one template per candidate driver per ride offer.
  A Redis lookup per render would swap a 1 ms query for a 1 ms network hop; a dictionary lookup swaps
  it for a pointer dereference. What has to be shared is the *invalidation*, and that is one pub/sub
  message on `RedisKeys.ContentInvalidationChannel` carrying a dataset list.
- **The TTL is a ceiling and the purge is the usual case.** The definition of done is "a template
  change is visible to notification-svc within the documented cache TTL" (D7' §4.2, 300 s).
  `CacheInvalidationTests` asserts both halves against two independently built services: with the
  purge on, the other replica sees the new version without the clock moving; with it off, the old
  version survives to 299 s and the new one is served at 301.
- **Expiry is measured on `TimeProvider`, and nothing sweeps.** An entry is checked when it is read,
  so there is no timer, no background scan, and a test can advance a `FakeTimeProvider` across a
  five-minute TTL in a millisecond.
- **The ETag is a digest of the payload, computed once per cache load.** Not per request (it would
  hash the same bytes on every call) and not from `max(updated_at)` + a row count (which would miss a
  change that reused a timestamp). The cities query carries `ORDER BY sort_order, code` and the
  tie-break is load-bearing rather than tidy: `sort_order` defaults to 0, so ties are possible, and an
  unstable row order would change the validator on every read and defeat the caching AL-27 depends on.
- **The two public endpoints are public because their screen precedes sign-in.** SCR-DA/DI-002 and
  SCR-PA/PI-002 draw the carousel above the language and city pickers, before any OTP. Both answers
  are cacheable with the same `max-age` — a difference between them would show up as one half of one
  screen going stale on a different schedule from the other.
- **The carousel returns all three languages at once and has no `lang` parameter.** The language
  picker is on the same screen, so the client re-renders the slides from the response it already has
  when the reader switches language. A `lang` parameter would mean a round trip per toggle on the
  slowest connection any user of this platform has.
- **A draft cannot shadow the published version.** `status = 'published'` is inside the CTE that picks
  the maximum version, not a filter over its result, so an unapproved edit cannot become the maximum
  and hide what is current. `ix_notification_templates_published` is the partial index behind it.
- **The approval workflow is two routes because it is two decisions.** `PUT` drafts version `n+1`;
  `POST …/approve` publishes it and records `approved_by` alongside `created_by`. Collapsing them
  would make `approved_by` a column that always names the author — D-35's four eyes, silently absent.
  `Content:PublishOnEdit` is the escape hatch, the response's `status` always says which happened, and
  the switch is announced at start-up.
- **One approver per version.** A second approval is a `409`, not an overwrite of who signed it off,
  and a version that does not exist is a `404` — told apart by reading the version's own status.
- **An unknown template key is a `404`, not a new template.** A key is only content if some service
  renders it (C005's own note on the four seeded keys: "inventing further keys would put strings in
  the database that no service resolves"), so a new key ships in a migration beside the code that
  sends it. This route edits the *wording* of one that exists.
- **The three languages of a template must interpolate the same `{{placeholders}}`.** Most of D6'
  I-29.2's templates carry `{{link}}` — the package-tracking link, the proxy-ride link, the 5-minute
  pickup-confirm link — and a Sinhala body that lost it in translation is an SMS with no link, sent to
  the one recipient who has no app to find another way in. Both directions are refused: a missing
  placeholder loses a value, an invented one is delivered literally.
- **An absent language falls back, says which it served, and logs.** `content.yaml` promises the
  fallback and refusing to serve would be worse — an undelivered ride offer is a driver who never
  learns about a fare. But 1307's trigger means the publish path *cannot* create an incomplete key, so
  reaching the fallback at all implies a row written around this service, which is why it is a warning
  rather than a silent resolution. The response's `language` is what was actually served.
- **A `?lang=` value is normalised, not matched literally.** `si-LK`, `ta_IN` and `SI` all resolve;
  anything else is English. A client that sent its device locale verbatim gets its language rather
  than English, which is the difference between a working picker and one that quietly does nothing.
- **Presentation order is Sinhala first (AL-26); fallback order is English first.** Two different
  questions — what to draw, and what to serve when the asked-for language is genuinely absent — and
  one shared order would answer one of them wrongly.
- **A broadcast's window is applied per request, not at load time.** The cache holds every row that
  has not ended; the start and end are compared against the clock on each read. Caching the filtered
  answer would hold a scheduled banner back for up to a TTL after its start time and would need a
  cache entry per role/app combination. Start is inclusive, end is exclusive, so two back-to-back
  broadcasts are never both up.
- **An audience selector may only say what a bearer can answer.** `role` and `app`, and the publish
  path *refuses* anything else — including C005's own `city` example, because
  `iam.users.operating_city_code` is not on the token and reading another bounded context's row for a
  banner would put an availability dependency on it. A predicate that could only be ignored at read
  time would mean an admin believing an announcement was targeted while the whole island received it.
  The role test is over the caller's whole role set (AL-06), so a driver who also books rides sees the
  driver announcement.
- **Authoring is Admin and Super Admin only.** D3' says "admin"; URD §2.3's content row gives the
  other four back-office roles no editorial cell, and a Support CSR rewriting every push notification
  on the platform is not a permission any spec grants. The narrower gate widens later without a
  migration.
- **The audit row is admin-bff's.** D-35's immutable log is `audit.events` and C065 owns it: every
  admin call arrives through that BFF, which records the actor and both images for the whole portal. A
  second audit row written here would double-count every edit and leave the two copies to disagree.
  What this service contributes is the version history — a permanent after-image, queryable.
- **The internal template read is guarded here rather than at the edge.** Every other `mTLS internal`
  family on this platform lives under `/v1/internal/**`, which the gateway refuses (C008). D3' prints
  this one as `GET /v1/content/templates/{key}`, and `gateway-routes.json` forwards `/v1/content/**` —
  so the guard is `Content:InternalApiKey`, compared in fixed time, answering `404` exactly as the
  gateway does. **Unset means open**, unlike registry-svc and trip-state-svc, which unmap theirs: a
  template body with placeholders is not a secret, and unmapping the route would stop every
  notification on the platform rendering with the failure landing on notification-svc. Announced
  loudly at start-up. The purge route *is* unmapped without the key, because that one is a write.
- **No silent caps.** `Content:MaxFaqItems` and `MaxBroadcasts` bound the two list reads; both ask for
  one row more than the cap so a full page can be told from a truncated one, and both log when it
  bites. The broadcast load is also bounded *in time* — one TTL into the future and no further —
  because rows come back newest-scheduled first, and without that a batch of announcements scheduled
  for next month would fill the limit and push today's live banner out of the answer.
- **The cache is keyed by nothing a caller supplies, except one thing that is bounded.** The FAQ is
  cached per *language* and the category filter is applied to the cached rows: keyed by
  `(language, category)`, `?category=*` and "no category" would collide on any sentinel a category
  could also contain — poisoning the unfiltered answer for a whole TTL — and a loop over random
  categories would grow the cache without bound. The one caller-supplied key that remains is the
  template key on the internal render route, and `Content:MaxCacheEntries` is the backstop for it: at
  the ceiling, expired entries are dropped and reads are served uncached rather than the process
  growing until it is killed.
- **There is a command log, and it is for one route.** R-14's replay matters where a repeated request
  would double an effect. An approve cannot (a second one is a `409` by the version's own status) and
  a purge cannot (it is idempotent by nature, and `content.yaml` marks it `x-idempotency-exempt`,
  which the route honours with `AllowMissingIdempotencyKey`). `POST /v1/admin/content/broadcasts`
  can: a proxy retry or a portal double-submit would put a **second identical banner** in front of
  every user on the platform, and no natural key would collide. `content.command_log` (1307) is the
  table, shaped like `registry.command_log` minus the aggregate id.
- **Every switch-off is announced at start-up.** The same rule query-svc, fanout-svc and
  fleet-health-svc are written under: content is served, nothing errors, and the difference only shows
  up as a notification sent with last month's wording, an edit nobody approved, or a template surface
  anyone can read.

## Schema this service added

`db/migrations/1307__content_publishing_workflow.sql` and
`db/migrations/1903__seed_content_onboarding.sql`. Every object is a micro-change-set in the C045
handoff, because §14's three tables are five, six and five columns wide.

| Object | Why |
|---|---|
| `notification_templates.status` / `approved_at` / `created_by` | D3' calls the admin route a versioned edit with an *approval workflow*; §14 gives a `version` and an `approved_by` — a *who* with no *whether* and no *when*, so there is nowhere to put an edit that has been written and not yet approved |
| `trg_notification_templates_trilingual` | the fence, as a constraint. A row trigger cannot express it (the invariant is over the *set* of rows sharing a version), so it is deferred to COMMIT — which is also what makes 1902's twelve-row seed valid |
| `ix_notification_templates_published` | "current" is now the highest *published* version, and every render goes through this lookup |
| `broadcasts.ends_at` / `created_by` | `GET /v1/content/broadcasts` serves what is "currently in force" and §14 gives `scheduled_at` alone, so either every banner is permanent or the rule is unimplementable |
| `ix_broadcasts_active` | C005's `ix_broadcasts_scheduled` is ascending and `WHERE scheduled_at IS NOT NULL`, so it cannot serve "newest window covering now" — and §14 makes the column nullable, so the read has to answer for rows this service did not write |
| `content.onboarding_slides` (+ seed) | AL-28's three slides per audience are an ordered pair of trilingual strings plus an illustration; `notification_templates` has no illustration and no ordering, so putting six slides there would mean twelve invented keys and no way to say which slide is second |
| `content.is_trilingual_text` + `ck_broadcasts_trilingual_strict` | `?&` is a key-presence test, so C005's constraint admits a null, an empty string and a number in any of the three languages — and this service's reader refuses to serve such a row at all, so one would take a whole list endpoint down |
| `content.command_log` | R-14 replay for `POST /v1/admin/content/broadcasts`, the one route here whose repetition would double an effect. Same micro-change-set C020/C021/C034/C033 raised: D4' §5 prints DDL for `rides.command_log` only |

`migrate-verify.sh` now expects **5** content tables, not 3, and carries a C045 section: the deferred
trigger's existence (including the version-move hole a NEW-only check would leave), the seeded
carousel, and thirteen rejection checks.

## Contract changes this component made

`content.yaml`, all recorded in the C045 handoff:

| Change | Why |
|---|---|
| `GET /v1/content/faq` | the C045 deliverable names it and C053's fence says support-svc serves FAQ but does not author it |
| `GET /v1/content/onboarding/{audience}` | AL-28 says "strings/illustrations served by content-svc" and D3' prints no route |
| `POST /v1/admin/content/{key}/approve` | D3' calls the edit route an approval *workflow* and the approval step had no route |
| `GET /v1/admin/content/{key}` | a draft nobody can read is a draft nobody can approve |
| `POST /v1/admin/content/broadcasts` | US-14.8's banner had a reader and no writer in any contract |
| `POST /v1/internal/content/cache/purge` | the invalidation path for the one dataset this service serves and admin-bff writes |
| `NotificationTemplate.placeholders` | lets notification-svc check it holds every variable before it renders, and makes the cross-language rule visible on the wire |
| `TemplateStatus` / `TemplateVersion` / `BroadcastAudience` / `FaqArticle` / `OnboardingSlide` | the shapes the six new operations return |
| `404` on `PUT /v1/admin/content/{key}` given a meaning | an unknown key is not a new template |

## Not here, and named rather than stubbed

- **FAQ authoring.** C053's fence puts ownership here, but there is no admin FAQ screen in D2', no
  route in D3', and — the blocking part — `content.faq_articles` has **no key linking the three
  translations of one article**: the three are sibling rows with a generated UUID each, so a
  trilingual editor has nothing to address. Migration 1902's twelve rows are the day-0 set. Adding an
  `article_key` and an editor is a decision about a screen and a column; raised in the handoff.
- **Placeholder substitution.** notification-svc's (C051). A GET with no body cannot carry values, and
  the contract's response is the template rather than a rendered message. This service reports the
  placeholder *set* so the renderer can check itself.
- **The launch-city CRUD.** admin-bff's (C065), by the D3' note's own wording
  (`POST/PATCH /v1/admin/config/cities`, audited D-35). This service serves the list and offers the
  purge route so a new city does not wait out a TTL. **Nothing calls that route yet.**
- **`audit.events`.** admin-bff's (C065). See the rules above.
- **Fare-tariff display strings.** The scope line names them; `fares.tariffs` (1001) holds numbers and
  a `vehicle_type`, and the display string for a tier is the canonical type's label — which lives in
  the apps' own resource files (AL-09's vocabulary, D2' §A) and in no `content.*` table. Nothing was
  invented for it; raised in the handoff.
- **The Dockerfile.** `infra/docker-compose.dev.yml` already carries a `content-svc` cluster
  destination pointing at the combined `app-services` container, which is where D7' §2.1 puts this
  service.

## Configuration

Every knob is documented at its declaration in `ContentOptions` and in `infra/env/.env.app.example`.

| Setting | Default | Where it comes from |
|---|---|---|
| `CacheTtl` | 300 s | D7' §4.2 `Cache__Ttl`. **Also readable as `Cache:Ttl`**, which is how that file spells it; the service's own key wins |
| `CacheEnabled` | on | off ⇒ a database round trip per notification render |
| `InvalidationEnabled` | on | off ⇒ the promise narrows from "immediately" to "within `CacheTtl`" |
| `PublishOnEdit` | **off** | on ⇒ no approval step, and `approved_by` records the author |
| `InternalApiKey` | unset | **unset leaves the template read open**, not unmapped — argued above |
| `AssetBaseUrl` | unset | **no spec**; unset is intended. Set it to move AL-28's artwork to a CDN |
| `MaxFaqItems` / `MaxBroadcasts` | 500 / 50 | **no spec**; bounds, and truncation is logged |
| `MaxCacheEntries` | 1 000 | **no spec** — a backstop, not a working limit; the five datasets need about a dozen entries between them |

`ConnectionStrings:Postgres`, `ConnectionStrings:Redis` and `Jwt:*` are required. `CommandLog:*`
defaults to `content` / `command_log` with no aggregate-id column (set in `ContentApplication`,
overridable). There is no `Kafka:BootstrapServers` and no `Outbox:*`, and there must not be — see
`ContentApplication` for why each is off.
