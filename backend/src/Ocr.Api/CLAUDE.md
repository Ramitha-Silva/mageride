# ocr-svc (C054) — document extraction, and the PII redaction pass in front of it

Stack: .NET 10 Minimal API + Dapper over Npgsql + OpenCV (OpenCvSharp) + the `tesseract` CLI +
Gemini Flash 3.0 over HTTP. References `MageRide.Shared` (C002).
**No Redis, no Kafka, no outbox, no command log, no bearer** — see `OcrApplication` for why each
is off.

**Verify:** `dotnet test backend/src/Ocr.Api.Tests -c Release`

`backend/contracts/ocr.yaml` is normative for this surface and wins over this file and over the
code. The whole file is a Δ C054 micro-change-set: **no specification gives ocr-svc an API.**

## Native dependencies — read this before wondering why nothing extracts

| What | Debian/Ubuntu | Why it is not optional |
|---|---|---|
| `tesseract` on `PATH` | `tesseract-ocr` | D6' §7.5's fallback **and** ADD §12.5's source of ID-number bounding boxes |
| OpenCV native (`libOpenCvSharpExtern.so`) | `libgtk-3-0t64`, `libatomic1` | the face blur and every raster operation in the pre-pass |
| a Haar face cascade | `opencv-data` | the model D-36's face blur runs |

**Missing any of them is not a crash — it is a posture.** The service starts, says so at `ERROR`,
reports `/health/ready` **degraded**, and extracts every document on the on-prem path with every
field flagged for review. What it will *not* do is send anything to Gemini. That is D-36 failing
closed, and it is deliberate: `docs.uploads` keeps flowing, drivers keep onboarding, and AL-27's
auto-approval is simply unreachable until the dependency is installed.

The suite and CI's backend leg install all three. `infra/docker/Dockerfile.ocr` is this service's
image, because the shared `Dockerfile.service` publishes onto Alpine and the OpenCV native build is
glibc — ocr-svc's image needs `apt-get install` regardless, for Tesseract.

## What this service is

| Endpoint | Auth | Spec |
|---|---|---|
| `POST /v1/internal/ocr/extractions` | internal key | **Δ C054** — D6' §7.5 draws the hop and D3' has no ocr-svc section |

| Table | Read | Written |
|---|---|---|
| `docs.uploads` | every extraction resolves one | **nobody, for onboarding** (C125) — this service only stamps NFR-28's `auto_delete_at` when it is NULL |
| `docs.extractions` | — | **this service**, one row per pass (D6' §7.5) |
| `registry.document_fields` | — | **registry-svc** (C029). See "the deliverable that moved" |

## The three fences, and how each is held structurally

- **The D-36 pre-pass runs before Gemini. No exceptions.** Held twice over. First by the *type*:
  `GeminiFieldExtractor.ExtractAsync` takes a `RedactedDocument`, a type whose constructor only
  `RedactionPipeline` can reach, so there is no overload that accepts the bytes off object storage
  and no way to write one by accident. Second on the *wire*: `PerimeterGuardHandler` sits outermost
  on that client and refuses any request carrying an image whose sha256 the redactor did not
  register — which catches the payload the type cannot see (a hand-assembled body, a future
  extractor, another provider's field name).
- **Tesseract is a required fallback, not an extra.** Gemini down, refusing, unconfigured, and
  OpenCV missing all land on the on-prem path, which still returns fields. They are capped at
  `Ocr:TesseractConfidenceCeiling`, below the auto-verify threshold, and the option validator
  refuses a configuration where it is not.
- **This service decides FIELD-level verified/pending, and nothing above it.** No code here writes
  `registry.document_fields`, `registry.onboarding_steps` or `registry.vehicles`, and there is no
  connection string that would let it.

## The deliverable that moved

The C054 manifest lists *"writes to `registry.document_fields` with confidence + source and the
derived verify_status"*. **This service does not write that table — registry-svc does** (C029), and
that is the reconciliation of two instructions that cannot both be followed:

- the C029 handoff's own fence: *"Return fields with confidences and nothing else: whether a field
  is pending, a step `pending_review` or a vehicle APPROVED is decided here [registry-svc], because
  AL-30 makes those properties of tables ocr-svc does not own."*
- two writers of one table would double-insert every field on every extraction, and
  `registry.document_fields` has no natural key to collide on.

So the deliverable is met *through* the seam: ocr-svc produces the value, the confidence, the
source and its own derived verdict; registry-svc persists them. `verifyStatus` is on the wire and
registry-svc deliberately ignores it, re-deriving the same answer from the same confidence against
its own threshold — which is why `Ocr:ConfidenceThreshold` and `Registry:OcrConfidenceThreshold`
have the same default and the same argument at both declarations. Raised in the C054 handoff.

## Rules that are load-bearing

- **The on-prem read happens on every document, before the redaction, whether or not Gemini will
  be called.** ADD §12.5 gets its mask boxes from Tesseract, so `no Tesseract ⇒ no boxes ⇒ no
  redaction ⇒ no Gemini` — one chain with no branch that skips a link. The same `OcrPage` is then
  the fallback extractor's input, so the slowest step in the pipeline runs once.
- **Every redaction failure is a refusal, never a partial pass.** There is no path that returns an
  image with *some* of the pass applied. "Blur what we found and go" is a pipeline whose D-36
  compliance depends on whether a library loaded, which is not a property anybody can audit.
- **An empty face list is not a failure; an unavailable detector is.** An insurance certificate has
  no portrait on it. The two are indistinguishable from the result, which is why
  `IFaceDetector.IsAvailable` exists separately and is checked *before* the document.
- **Faces are blurred and ID numbers are filled, and the difference is deliberate.** A blur leaves a
  document that still reads as a document — the model can see a portrait belongs there, which is
  what stops it hallucinating a field into the space — while removing the biometric. A number
  cannot be blurred: a strong enough blur is still invertible for a nine-character string over a
  known alphabet at a known position.
- **The redacted copy is re-encoded as PNG even when the original was a JPEG.** A second JPEG
  generation over a freshly blacked-out rectangle leaves ringing along its edges — faint, and a
  partial reconstruction of the glyphs that were under it.
- **The prompt tells the model the image was redacted.** Without it, a black rectangle reads as a
  printing artefact and the model invents a plausible NIC for the space — which is exactly what
  I-25.1's "the value is captured from the structured response" has to be protected from.
- **Confidence is asked for per field, with a rubric.** AL-29, BR-25.2 and D6' §7.5 hang the whole
  verdict on "below threshold", and a model asked for a number with no rubric returns 0.95 for
  everything. The rubric is the legibility of *that field's own characters*, not the model's belief
  in its own answer.
- **`reg_no_match` is computed here and is never the model's opinion.** D5' §14.1a's photos verdict
  is a comparison against a plate the caller supplies; C029's seam hands it over precisely so the
  comparison and its normalisation live in one place. A model that returned the field would be
  answering a question it was never given the other half of.
- **Plates compare on their alphanumerics, and nothing else is forgiven.** `WP QA-1234`,
  `WP-QA-1234` and `WPQA1234` are one registration; `WP-QA-1284` is a different vehicle. No
  `O`↔`0` or `I`↔`1` folding — over-strictness costs an officer one glance, under-strictness puts
  an unverified vehicle on the road. `reg_no_match` fails in *both* directions: a confident
  mismatch is pending, and so is a match read off an illegible plate.
- **Reading a plate is done on tokens, not on a regex over the page.** Tesseract renders the
  fixture's separators as `WP—-QA-—1234`; splitting on everything non-alphanumeric makes the
  separator irrelevant, and keeping the token boundaries is what stops `EXPIRY 2029` — six letters
  and four digits, the exact canonical plate shape — being read as a registration.
- **The default page-segmentation mode is 3, not 11.** "Sparse text" is the intuitive choice for a
  form and reads one identically to PSM 3 — but returns **nothing at all** for a framed number
  plate, which is the one document step 4/4 cannot be blind on. A page that comes back empty is
  retried under the other mode before it is called unreadable.
- **Dates are read day-first, and there is no month-first format in the list.** Sri Lanka writes
  `30.04.2029`. "Whichever parses" is how `03/04/2029` becomes March or April depending on nothing,
  moving an expiry by up to eleven months silently — and E-03 acts on that value.
- **Every field on the fallback path is capped below the auto-verify threshold.** Not because
  Tesseract is untrustworthy — its per-word confidences are honest — but because this path has no
  layout model: it finds a date near a label, and AL-27 approves a vehicle with no human
  involvement on the result. It is D6' §7.5's own "below threshold → manual admin review" expressed
  as a ceiling rather than as a hope that the numbers come out low.
- **The document-level confidence is the lowest field, not the mean.** A certificate whose insurer
  read at 0.99 and whose expiry read at 0.30 is one nobody should act on, and 0.65 describes
  neither number.
- **A required key that did not extract is still returned**, with a null value and `pending`, so
  the officer queue shows a row to fill rather than an absence to notice. C029's rule (3), from
  this side of the seam.
- **A key nobody asked for is dropped.** `field_key` is free text, so an invented key would land in
  the officer queue as a row about a field the wizard has no screen for.
- **Nothing about a document is an HTTP error.** Unreadable, unavailable, both engines down — all
  `200` with `succeeded: false`. The caller has a step to save either way (D5' §14.1a), and a `5xx`
  would put registry-svc's retry between a driver and their next screen. The only `4xx` is a
  malformed request or an unknown document kind.
- **The queue is in process, bounded, and full is a refusal.** The pass is idempotent and its caller
  is a synchronous hop with a 30-second budget, so a document that outlived the process has
  outlived the request that wanted it; a durable queue would deliver a result to a caller that
  stopped waiting. Past the capacity the honest answer is that this service cannot take it.
- **`storage_url` is a value from a table this service does not own, and is not followed anywhere.**
  A path that resolves outside `Ocr:Storage:Root` is refused, and `http(s)` sources are off unless
  asked for by name — otherwise one `docs.uploads` insert reads the cluster's metadata endpoint.
- **The kernel's deny-by-default fallback policy is removed here, and replaced by a test.** With no
  authentication scheme registered — this plane carries no token — `RequireAuthenticatedUser`
  cannot be satisfied, and every *unmatched* path answers 500 rather than 404.
  `Every_route_on_this_service_is_health_or_key_gated` is what holds the posture instead: nothing
  may be mapped here except the probes and the key-gated internal group.
- **Every switch-off is announced at start-up**, and here for its own reason: **a disarmed redactor
  looks exactly like a working one from the outside.** Documents go in, fields come out, drivers
  onboard — and every vehicle silently needs a Verification Officer, because AL-27's auto-approve is
  unreachable without a Gemini path. Nothing else on the platform can tell the difference.

## Schema this service added

`db/migrations/1310__docs_extraction_provenance.sql`, a micro-change-set recorded in the C054
handoff. ADD §12.5 asks for a *"document processing log: hash + policy version + redaction-pass
version stored per extraction"* and 1301 gives it one BOOLEAN.

| Object | Why |
|---|---|
| `raw_sha256` | identifies *which file* was processed, and survives NFR-28 deleting it |
| `redacted_sha256` | what actually left the perimeter; the value `PerimeterGuardHandler` admits |
| `redaction_policy_version` / `redaction_pass_version` | ADD §12.5 verbatim. Without them a policy change cannot be scoped to the extractions it affected — which §12.5's required privacy impact assessment is exactly the reader of |
| `faces_blurred` / `identifiers_masked` | zero faces on an insurance certificate is correct; zero faces on every licence for a week is a broken cascade, and without a count the two are identical |
| `engine` + `ck_extractions_engine` | D6' §7.5 has two extractors and §8.3 a documented fallback between them; a row that cannot say which produced it cannot answer "how much of the officer queue is Gemini being down" |
| `ck_extractions_gemini_is_redacted` | the D-36 invariant in the last place able to refuse to record its violation. `NOT VALID` — pre-C054 rows carry no engine and are out of scope |
| `ix_extractions_fallback` | the fallback-volume question above, partial so the index is the size of the fallback traffic |

## Contract changes this component made

| Change | Why |
|---|---|
| `backend/contracts/ocr.yaml` (new) | D3' has no ocr-svc section at all; D6' §7.5 draws the hop and names no shape |
| `_shared.yaml` `FieldSource` → `[ai, manual]` | it said `[ocr, manual]`, and `ocr` is a value `ck_document_fields` cannot store (D4' §2 / migration 0305) |
| `_shared.yaml` `VerifyStatus` → `[auto_verified, pending, confirmed]` | it said `[pending, confirmed, rejected]`, omitting the state almost every field is in. A generated client (C012/C013) would have failed to deserialise every field registry-svc has ever returned |
| `_shared.yaml` `ExtractedField.key` example | `licenceNo` → `licence_no`; the column is the stored key, not a wire name |

## Not here, and named rather than stubbed

- **The upload surface.** `docs.uploads` still has no writer for onboarding — registry-svc resolves
  ids it did not create and says so, and this service reads bytes it did not put there. That is
  C125's, together with the S3 client behind `IRawDocumentStore`. A vehicle cannot be onboarded in a
  deployment where nothing fills that table.
- **NFR-28's sweeper.** This service writes the deadline; nothing in this build deletes on it.
- **The Verification-Officer queue.** C062's. This service produces the pending fields; registry-svc
  writes them and emits `document.review_required`.
- **A licence-class → `vehicle_type` mapping.** AL-29 stores `allowed_vehicle_types` as printed
  (`A1,B,C1`); no spec in this build maps a Sri Lankan class to a `registry.vehicles.vehicle_type`,
  and inventing one would put an unstated rule between a driver's licence and what they may drive.
- **Azure Document Intelligence.** ADD §12.5 names it as an alternative fallback "(regulated)". One
  fallback with a hard ceiling is what D6' §7.5 specifies; a second external model would need its
  own D-36 argument.
- **PDF documents.** The content sniffer recognises them and Tesseract will read one, but nothing
  rasterises a multi-page PDF and AL-43's capture surface produces images. Named in the handoff.

## Configuration

Every knob is documented at its declaration in `OcrOptions` and in `infra/env/.env.app.example`.

| Setting | Default | Where it comes from |
|---|---|---|
| `InternalApiKey` | unset | **unset ⇒ `/v1/internal/ocr/**` is not mapped**: no document can be extracted at all, and no Mode-C vehicle ever auto-approves |
| `ConfidenceThreshold` | 0.80 | **no spec** — AL-29/BR-25.2/D6' §7.5 all say "below threshold". The same value and argument as `Registry:OcrConfidenceThreshold`; bounded at 0.5 |
| `TesseractConfidenceCeiling` | 0.60 | **no spec** — D6' §7.5's "below threshold → manual admin review", made structural. Validated to be below the threshold |
| `RawRetention` | 90 d | NFR-28. Written to `docs.uploads.auto_delete_at` when it is NULL, never overwritten |
| `Storage:Root` | unset | **not object storage** — D-36's SSE-KMS bucket, when a client exists (C125). Unset ⇒ nothing can be read |
| `Storage:MaxBytes` | 16 MiB | **no spec** — the same bound as `Support:ScreenshotMaxBytes`; refused before decoding |
| `Storage:AllowHttpSources` | off | on ⇒ this service will fetch a URL another service wrote into a row |
| `Gemini:Enabled` · `BaseUrl` · `ApiKey` | on · Google · unset | **unset ⇒ every document takes the on-prem path** and AL-27 is unreachable |
| `Gemini:Model` | `gemini-flash-3.0` | D6' §7.5 and ADD §12.5 name the version |
| `Gemini:Timeout` · `Attempts` | 20 s · 3 | D6' §8.3 ("OCR 30 s" is the whole job's; the model gets the larger part) |
| `Tesseract:ExecutablePath` | `tesseract` | **absent ⇒ nothing is sent to Gemini either** — see the chain above |
| `Tesseract:Language` | `eng` | the machine-readable fields on these documents are Latin script; D-26 is about strings MageRide authors |
| `Tesseract:PageSegmentationMode` · `Fallback…` | 3 · 11 | **no spec** — 11 returns nothing for a number plate; see the rule above |
| `Tesseract:WorkRoot` | *(temp dir)* | where a raw document is staged for the child process. A mount, not `TMPDIR` |
| `Redaction:FaceCascadePath` | *(probed)* | `opencv-data`'s location. Not found ⇒ no face blur ⇒ no Gemini |
| `Redaction:DetectionWidth` · `MinimumFaceFraction` | 1024 · 0.08 | **no spec** — bounds the detector's work, and stops a 20 px false positive blurring a number plate |
| `Redaction:BlurDivisor` · `MinimumBlurKernel` | 6 · 21 | **no spec** — the kernel is derived from the region so one setting redacts a 480 px photo and a 4 000 px scan alike |
| `Redaction:PreserveJpeg` | off | see the re-encode rule above |
| `Queue:Capacity` · `Workers` · `JobTimeout` | 256 · 4 · 30 s | **capacity/workers have no spec**; the timeout is D6' §8.3's |

`ConnectionStrings:Postgres` is required. There is no `ConnectionStrings:Redis`, no
`Kafka:BootstrapServers`, no `Outbox:*`, no `CommandLog:*` and no `Jwt:*`, and there must not be —
see `OcrApplication` for why each is off.

## The caller's half

`Registry:OcrBaseUrl` is what turns registry-svc's `UnconfiguredDocumentExtractionClient` into the
real hop (`OcrDocumentExtractionClient`); `Registry:OcrInternalApiKey` must equal
`Ocr:InternalApiKey` or every extraction is a 404. There is deliberately **no retry** on that hop:
ocr-svc already retries the leg that fails (Gemini, D6' §8.3) and has the on-prem engine behind it,
so a retry from registry-svc would re-run a whole pass — a second Tesseract read and a second
`docs.extractions` row — while a driver waits on a step save.
