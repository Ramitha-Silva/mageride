# support-svc (C053) — in-app FAQ, user tickets, and the agent queue behind them

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).
**No Redis, no Kafka and no outbox** — see `SupportApplication` for why each is off.

**Verify:** `dotnet test backend/src/Support.Api.Tests -c Release`

`backend/contracts/support.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is

The two things somebody reaches for when the app has not done what they expected: the help page, and
the form that puts a human on it.

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/support/faq?lang=&category=` | Bearer | D3' support-svc, US-16.1 |
| `GET /v1/support/faq/{articleId}?lang=` | Bearer | D3' route table |
| `POST /v1/support/tickets` | Bearer | US-16.2 |
| `GET /v1/support/tickets/{userId}` · `/{ticketId}` | Bearer | D3' route table |
| `POST /v1/support/screenshots` | Bearer | **Δ C053** — US-16.2's attachment had no upload surface |
| `GET /v1/support/screenshots/{uploadId}` | **the signature** | **Δ C053** — `screenshotUrl` had no route to be a URL of |
| `GET /v1/internal/support/tickets` · `/{ticketId}` | internal | **Δ C053** — the queue admin-bff forwards |
| `POST /v1/internal/support/tickets/{id}/assign` · `/respond` · `/resolve` | internal | **Δ C053** — US-16.3, US-14.13 |

| Table | Read | Written |
|---|---|---|
| `content.faq_articles` | the two FAQ routes | **content-svc** (C045) — read-only here, structurally |
| `support.tickets` | every ticket route | **this service**, subscription-svc (C047) and fare-svc (C050) |
| `support.ticket_events` | the thread | **this service** |
| `docs.uploads` | the signed screenshot read | **this service**, for `kind='support_screenshot'` only |
| `support.command_log` | the kernel | **this service** |
| `iam.users` | — | **iam-svc** — only as a foreign-key target |
| `audit.events` | — | **admin-bff** (C065) — never touched here |

## The two fences, and how each is held structurally

- **FAQ content is content-svc's; this service serves and filters it.** Held by the *type*, not by
  care: `IFaqRepository` has three methods and all three are `SELECT`s, so there is no code path
  here that can author, edit or delete an article. `Nothing_in_this_service_can_write_an_article`
  asserts it by reflection, so a method added later — an editor, a "seed if missing", a cache warm
  that upserts — fails the suite rather than quietly crossing the line.
- **Ticket resolution UI is the Admin Portal's (C107); the RBAC-gated, audited front door is
  admin-bff's (C065).** What is here is the decision, on `/v1/internal/support/**`, which the
  gateway refuses at the edge (C008) and which is unmapped entirely without its key. No route on
  this service checks a Support CSR or Finance Officer role, because it never sees the agent's
  bearer — admin-bff does, and it passes the resolved `actorId` on the body.

## Rules that are load-bearing

- **The FAQ is read from Postgres, not fetched from content-svc.** The platform's established shape
  for a cross-context *read* (safety-svc reads `iam.users`, subscription-svc reads
  `registry.vehicles`), and the reason is availability: the FAQ is the screen somebody opens when
  something has *already* gone wrong, and a hop through another service means a content-svc outage
  takes out the help page. CLAUDE.md's outbox rule governs cross-service **state changes**; nothing
  here changes state.
- **The fallback order is requested → `en` → `si` → `ta`, the whole order is walked, and the answer
  says which language it served.** "Requested, else English" leaves a Tamil reader with nothing when
  an article exists only in Sinhala. Presentation order stays Sinhala-first (AL-26) — two different
  questions, and one shared order would answer one of them wrongly. Reaching a fallback at all is
  logged as a warning: it means content-svc's day-0 set is incomplete for a language D-26 promises.
- **The fallback is evaluated against the *filtered* answer.** A Tamil reader asking about wallet
  top-ups when only the English article exists gets the English one; falling back only when a whole
  language is absent would hand them an empty list and no explanation.
- **`{articleId}` names one row in one language, so serving another language means finding the
  sibling — and the sibling key is derived.** `content.faq_articles` has **no column linking the
  three translations of one article**; C045 found the same hole and left it open on purpose. The
  link is derived from `(category, sort_order)`, the pair migration 1902's seed makes unique per
  language and the pair `ix_faq_articles_lookup` leads with. It is a derivation, it is stated as one
  at `IFaqRepository.ListTranslationsAsync`, and the micro-change-set for a real `article_key` is
  raised again in the C053 handoff. That method is the one place that changes when it lands.
- **No cache.** content-svc caches these rows because it serves them on the notification render
  path, where the budget is a ride offer. Here the reader is a person who has just opened Help and
  the query is an index scan over twelve rows; a second cache in another process would only mean the
  same edit becoming visible at two different times on two screens.
- **The queue a ticket lands on is derived from its category, never stored.** `daily_fee_refund`
  (US-9.23) and `driver_qr_dispute` (AL-47) are Finance's — both end in money moving, and URD §2.3
  gives that to the Finance Officer; everything else is Support's (US-14.13). Derived because
  **`support.tickets` has three writers** — subscription-svc (C047) raises the refund claim itself,
  having checked the driver was in fact charged on the day they are disputing, and fare-svc (C050)
  raises the QR dispute — and a stored column would default exactly those rows onto the wrong queue.
  A pure function over `category` gets the same answer whoever wrote the row, and the queue query
  expresses it in SQL rather than filtering afterwards.
- **The screenshot is linked by id, and the id is a foreign key.** `support.tickets.screenshot_url`
  — §13's public-URL column — is written by nothing here. What the user gets is a link minted per
  read, HMAC-signed over `(kind, uploadId, expires)` and short-lived, so it cannot be cached with the
  ticket and cannot outlive the session that asked for it.
- **The legacy `screenshot_url` is read, and only an agent sees it.** fare-svc writes AL-47's
  QR-dispute evidence into that column, so *not* reading it would silently drop the attachment on a
  Finance-queue ticket this service is responsible for showing. It is projected onto `TicketRow` and
  never onto `TicketDetail`: an unsigned, uncontrolled URL in front of the complainant is precisely
  what the definition of done rules out, and admin-bff already gates and audits the agent's read.
- **The bytes are written before the row.** A crash between them leaves an orphan file, which
  NFR-28's deadline sweeps; the other order leaves a ticket pointing at nothing, which is a broken
  image on a complaint nobody can explain.
- **The signed read checks the `kind` as well as the signature.** The signature covers the kind, but
  the route reads `docs.uploads`, which also holds driving licences and bank statements. A signing
  key that ever leaked would otherwise be a key to somebody's NIC rather than to a screenshot.
- **A bad signature, an expired one and an unknown id answer identically.** Telling them apart tells
  somebody probing which half of a forged link to work on, and "that id exists" is itself something a
  forged link should not be able to learn.
- **A screenshot that is not attachable is `validation-failed`, not silently dropped.** Three
  refusals — no such upload, not yours, already on another ticket — with one message, for the oracle
  reason above. Dropping the id instead would leave the complainant believing their evidence was
  attached.
- **One screenshot belongs to one complaint.** Otherwise an id could be attached to a second ticket
  and the two would share an artefact whose deletion deadline belongs to neither.
- **Somebody else's ticket is `404`, not `403`.** The row scoping is inside the same answer as the
  lookup: a `403` would confirm the id names a real complaint, and a ticket id is guessable in
  exactly the way a complaint should not be. The path `{userId}` is checked against the token
  separately, and a malformed one is `403` — whatever it was, it was not the caller's.
- **There is no back-office exception on `GET /v1/support/tickets/{userId}`**, unlike
  subscription-svc's fee history. An agent has the queue, which is RBAC-gated and audited by
  admin-bff (D-35); a route that let any internal role page a named user's complaints from the app
  surface would be the same read with none of that.
- **Every move and the thread entry that records it commit together.** A status that changed with no
  event behind it is a resolution the user cannot see — the definition of done this component is
  measured against — and an event with no status behind it describes something that did not happen.
- **`from_status` is read under `FOR UPDATE`.** Without the lock two agents could both read `OPEN`
  and the one whose guarded update lost would have written a thread entry claiming a transition that
  never happened. The loser blocks, wakes to the new status, and its own guard turns it into the
  `409` it should be.
- **Every timestamp on these tables comes from Postgres.** `created_at`, `updated_at`, `assigned_at`,
  `resolved_at`, `ticket_events.at` and `docs.uploads.auto_delete_at` are all `now()`. A thread entry
  taken from one replica's clock beside a `resolved_at` taken from the database's is how a resolution
  comes to be stamped before the reply that resolved it — and that is not hypothetical, an earlier
  revision did exactly that and the thread sorted out of order. `TimeProvider` is used for one thing:
  the signed link's TTL, which is an application decision rather than a row.
- **Thread ids are UUIDv7.** The thread is read `ORDER BY at, id`, and two entries sharing an instant
  ordered by `gen_random_uuid()` would let a reply render above the transition that caused it.
- **A resolution happens once.** A guarded `UPDATE … WHERE status <> 'RESOLVED'`, so two agents
  produce one decision and the loser is told it was already resolved rather than overwriting who
  decided it. `404` and `409` are told apart by reading the row — one is a typo, the other is a race.
- **`assign` and `respond` are separate routes because they are separate decisions.** Collapsing
  `respond` into `resolve` would mean an agent asking a clarifying question had to close the ticket
  to be heard; `admin-bff.yaml` has a route for neither, which is why both are Δ C053.
- **A resolved ticket cannot be assigned or answered again.** Reopening is a decision, not a side
  effect of picking something up — and there is no route for it yet (see below).
- **The assignment is recorded and withheld from the user.** Who inside MageRide is handling a
  complaint is not the complainant's business, and a complaint about a named driver routed to a named
  CSR is exactly the pairing that should not be readable by the person who filed it. The row is
  written; `TicketEventKinds.UserVisible` is what withholds it. No thread entry carries an actor
  **id** on the wire at all — only a role.
- **The queue carries no thread.** It is a list of what is waiting; reading every ticket's whole
  conversation to render one screen would be a query per row, and the detail route is one click away.
- **`tripId` is stored unvalidated.** Migration 1303 leaves `ride_id` without a foreign key because
  the referent is polymorphic — a Mode C `rides.rides` id or a Mode A/B `trips.sessions` id — and
  resolving which would mean reading two other bounded contexts on the intake path. A wrong id costs
  a CSR one lookup; refusing the ticket costs the platform the complaint.
- **No silent caps.** `Support:MaxFaqItems`, `MaxPageSize` and `MaxThreadEvents` each ask for one row
  more than the cap so a full page can be told from a truncated one, and each logs when it bites.
- **Every switch-off is announced at start-up**, and here for its own reason: **a ticket nobody can
  resolve looks exactly like one nobody has got to yet.** The sheet submits, the row is written, the
  user is told their request was received, and there is no queue behind it.

## Schema this service added

`db/migrations/1309__support_ticket_thread.sql`. Every object is a micro-change-set in the C053
handoff, because §13 gives `support.tickets` one `admin_response TEXT` and the contracts already
promise more than that.

| Object | Why |
|---|---|
| `tickets.assigned_to` / `assigned_at` | the deliverable is "list, assign, respond, resolve"; §13 has no way to say a ticket is somebody's, so two CSRs answer the same one and the Finance pile cannot be worked as a queue |
| `tickets.resolved_at` / `resolved_by` | `support.yaml`'s `Ticket` already returns `resolvedAt` and §13 has no column for it; D-35's appealability needs the *who*, and `updated_at` cannot stand in because it moves on every reply |
| `tickets.screenshot_upload_id` | the definition of done says "links it by id, not by public URL", and §13's `screenshot_url TEXT` is the public URL that rules out. A FK onto `docs.uploads` (D-36) is what makes it structural |
| `ck_tickets_resolution` | a RESOLVED ticket with a null instant reads as still open in the app, and an OPEN one with an instant reads as answered on a queue nobody has answered. `NOT VALID`, because `resolved_at` is new and subscription-svc's rows have none |
| `ix_tickets_status_created` | 1303's `ix_tickets_open` answers "everything unresolved" and cannot serve `?status=RESOLVED`, which the admin-bff queue offers |
| `support.ticket_events` | "status transitions are recorded and visible to the user in the thread" — §13 holds one `admin_response`, so a second reply overwrites the first and a status change leaves no trace at all. **Not `audit.events`**: that is admin-bff's and is invisible to users; this one is *for* the user, and carries only what a user may read |
| `support.command_log` | R-14 per bounded context — the **twelfth** time this micro-change-set has been raised. `POST /v1/support/tickets` is why: a double-tapped Submit puts a second identical complaint on the queue and no natural key would collide |

`migrate-verify.sh` now expects **3** support tables, not 1, and carries a C053 section: the two new
tables, the five handling columns, the FK that replaces the URL, both indexes and four rejections.

## Contract changes this component made

`support.yaml`, all recorded in the C053 handoff:

| Change | Why |
|---|---|
| `POST /v1/support/screenshots` | US-16.2's "button to attach a screenshot" had **no upload surface anywhere on the platform** — registry-svc resolves `docs.uploads` ids and says outright that filling that table is not its job |
| `GET /v1/support/screenshots/{uploadId}` | `TicketDetail.screenshotUrl` is described as a signed URL and had no route to be a URL of |
| `GET /v1/internal/support/tickets` · `/{ticketId}` | the queue admin-bff forwards, and its detail |
| `POST …/{id}/assign` · `/respond` | C053 deliverables `admin-bff.yaml` has no route for |
| `POST …/{id}/resolve` | the decision behind `admin-bff.yaml`'s resolve |
| `TicketDetail.thread` + `TicketEvent` | the definition of done's "visible to the user in the thread" |
| `Ticket.queue` + `TicketQueue` | US-9.23's Finance routing had no way to be seen on the wire |
| `TicketRow` | the agent's view, shaped so `admin-bff.yaml`'s own `TicketRow` is a subset |
| `TicketRow.legacyScreenshotUrl` | fare-svc's AL-47 evidence lives in §13's old column; agent-only |
| `UploadedScreenshot` | what the upload returns |
| `FaqSummary.language` given a meaning | it is the language *served*, which the fallback makes different from the one asked for |

## Not here, and named rather than stubbed

- **FAQ authoring.** content-svc's (C045) by this component's own fence — and blocked there on the
  missing `article_key`, which is a decision about a screen and a column. Migration 1902's twelve
  rows are the day-0 set.
- **`audit.events`.** admin-bff's (C065, D-35). Every agent action arrives through that BFF, which
  records the actor and both images for the whole portal; a second audit row here would double-count
  every resolution and leave the two copies to disagree. What this service contributes is
  `support.ticket_events`, which is a different artefact for a different reader.
- **The wallet reversal a daily-fee refund claim ends in.** admin-bff's
  (`POST /v1/admin/drivers/wallet/{id}/reverse-fee`, US-14.11) against wallet-svc. This service
  routes the claim to the queue that decides it and moves no money.
- **subscription-svc's and fare-svc's direct writes of `support.tickets`.** The C047 and C050
  handoffs each say the write "becomes a forward to C053's ticket route" when this service lands.
  **Neither has been changed**, deliberately: the validation that makes each write correct — only
  subscription-svc can say whether the driver was in fact charged, only fare-svc holds the QR
  settlement — would have to move or be duplicated, and the routing here is derived from the category
  precisely so the existing rows land on the right queue either way. Raised in the C053 handoff as
  the follow-up it is, with the shape a forward would take.
- **Reopening a resolved ticket.** `ck_ticket_events_kind` admits `reopened` and nothing writes it:
  US-16.3 ends at "mark them as resolved" and no spec gives a user or an agent a way back. The value
  exists so that when a screen for it lands it is not a migration.
- **The "your ticket was answered" push.** notification-svc's (C051). This service moves the state
  and writes no `comms.*` row; it would be the first real consumer of a `ticket.*` event, and is why
  the outbox is named as absent rather than merely missing.
- **Ticket translation.** A description and an agent's reply are the words two people wrote; D-26's
  trilingual rule is about platform-authored strings, and machine-translating a complaint would put
  words in somebody's mouth on a record that may be appealed.
- **The Dockerfile.** `infra/docker-compose.dev.yml` already carries a `support-svc` cluster
  destination pointing at the combined `app-services` container, which is where D7' §2.1 puts this
  service.

## Configuration

Every knob is documented at its declaration in `SupportOptions` and in `infra/env/.env.app.example`.

| Setting | Default | Where it comes from |
|---|---|---|
| `InternalApiKey` | unset | **unset ⇒ `/v1/internal/support/**` is not mapped**: no ticket can be assigned, answered or resolved, and every one stays OPEN for ever |
| `ScreenshotRoot` | *(temp dir)* | **D-36 (Δ C063)** — bytes go to the kernel's `IObjectStore` (`AddMageRideObjectStore`): S3-compatible, server-side encrypted, presigned reads, and NFR-28's expiry applied by the bucket's own lifecycle rule scoped to the `ephemeral/` key prefix. This setting is now the **filesystem fallback's root**, used when `Storage__S3__Endpoint` is unset. The bucket is D7' §4.2's `Storage__ScreenshotBucket`; the viewer route now 302s to a presigned GET |
| `ScreenshotMaxBytes` | 8 MiB | **no spec** — the same bound as `Ride:ProofPhotoMaxBytes` and `Subscription:SlipMaxBytes`; the idempotency request buffer is raised to match |
| `ScreenshotRetention` | 90 d | NFR-28. Written to `docs.uploads.auto_delete_at`; the sweeper is not this service's |
| `FileLinkSigningKey` | unset | **unset ⇒ a key per process**: a link minted by one replica does not verify on another |
| `FileLinkTtl` | 15 min | **no spec** — long enough to open the image, short enough that a copied link is dead |
| `MaxFaqItems` | 500 | **no spec**; the same default as `Content:MaxFaqItems`, because the two read the same table |
| `MaxPageSize` | 50 | **no spec** — a bound on both list reads (D3' §0 caps a page at 100) |
| `MaxThreadEvents` | 200 | **no spec** — a backstop; truncation drops the newest replies, never the complaint, and is logged |

`ConnectionStrings:Postgres` and `Jwt:*` are required. `CommandLog:*` defaults to `support` /
`command_log` with no aggregate-id column (set in `SupportApplication`, overridable). There is no
`ConnectionStrings:Redis`, no `Kafka:BootstrapServers` and no `Outbox:*`, and there must not be —
see `SupportApplication` for why each is off.
