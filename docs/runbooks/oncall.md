# On-call — the rota, the escalation, and the status page

Not alert-driven. This is the document that says who hears an alert, what they are promised, and
what the public is told. Routing: `infra/observability/alertmanager/alertmanager.production.yml`.

## First action — you have just been paged

1. **Acknowledge in PagerDuty.** That stops the escalation timer. It is not a claim that you have
   fixed anything.
2. **Open the runbook in the notification.** Every alert on this platform carries a
   `runbook_url`, and `verify-observability.sh` fails the build if the file behind it does not
   exist, so there is always one.
3. **Do the runbook's First action before diagnosing.** Each one opens with a single thing to do
   inside the first minute. That is deliberate: the most expensive minute of an incident is the
   one spent deciding where to start.
4. **If it is customer-visible, post to the status page before you investigate** — §5. Three
   minutes of "we are looking at it" is worth an hour of goodwill, and it costs nothing you need.

## 1. The three services, and what each promises

PagerDuty escalates within a service, so these are three different response promises. Putting
them in one would mean the loudest promise governs everything.

| Service | What routes to it | Ack window | Escalates to |
|---|---|---|---|
| **mageride-safety** | SOS dispatch latency (D-33) — the only alert whose subject is a person in danger | **5 min** | second responder at 5 min, engineering lead at 10 |
| **mageride-platform** | everything else that pages: stuck rides, no database leader, WAL not archiving, security signals | 15 min | second responder at 15 min, engineering lead at 30 |
| **mageride-tickets** | capacity triggers, a failed nightly dump, a pending restart | next business day | nobody; it is a queue |

**A `ticket` must never wake anybody.** A rota that is woken to do capacity planning stops
answering the pager for the things that matter, and capacity work done half asleep is how a
cluster gets a wrong number that survives for a year.

## 2. The rota

Two people at any time: a **primary** who takes the page, and a **secondary** who is the
escalation target and the person the primary calls when they need a second pair of eyes. One week
each, handover Monday 10:00 Asia/Colombo.

The handover is a conversation, not a calendar entry. It covers: what fired last week and whether
it is really resolved, anything deliberately silenced and when the silence expires, and any
change going out this week.

**Silences are never open-ended.** Every silence gets an expiry and a comment naming the reason;
a silence that outlives its reason is an alert that has been deleted without anyone deciding to
delete it. `amtool silence query` at handover.

## 3. When the routing is wrong

If a `ticket` woke you, or a page arrived that should not have, that is a defect in
`alertmanager.production.yml` and it is fixed the same way as any other: a PR. Do not silence it
and move on — the next person gets the same page.

The two knobs:

* **severity** on the Prometheus rule decides whether it wakes anybody. It belongs with the rule,
  because whether something is worth waking for is a property of what it means, not of who is
  listening.
* **the route** in `alertmanager.production.yml` decides which service and how often it repeats.

## 4. Escalation beyond engineering

| Situation | Who, and when |
|---|---|
| Passenger or driver safety (SOS not dispatched, live tracking wrong on an active ride) | engineering lead immediately, and the operations lead — this is the one class where the business must know before it is fixed |
| Money (payouts, wallet balances, double-charges, an unbalanced ledger) | engineering lead + finance owner within 30 min. **Do not attempt a correcting write.** Every manual state change goes through admin-bff so it lands in `audit.events` (D-35) |
| Personal data (an exposure, a PDPA request that cannot be served) | engineering lead + the data-protection owner within 1 h. PDPA has statutory clocks |
| Sustained full outage > 30 min | engineering lead notifies the project owner; status page goes to "major outage" and stays updated every 30 min |

## 5. The status page

**`status.mageride.lk`, on a provider outside this infrastructure.** A status page hosted in the
cluster it reports on is a status page that goes down with it — which is the one moment it exists
for. Any hosted provider (Atlassian Statuspage, Instatus, BetterStack) satisfies this; the
requirement is only that it shares no dependency with DOKS Singapore, Wasabi or Cloudflare R2.

### The five components it shows

Named for what a user recognises, not for a service:

| Component | Degraded when | Down when |
|---|---|---|
| Live vehicle tracking | positions delayed (`PositionE2ELatency…`) | `PositionPipelineSilent`, or fanout down |
| Booking a ride | offers slow, dispatch degraded | ride-svc or dispatch-svc down |
| Payments & wallet | callbacks lagging | fare-svc, wallet-svc or the gateway down |
| Driver & vehicle sign-up | OCR or document upload failing | registry-svc down |
| Apps & website | portals slow | the ingress or api-gateway down |

### The rules

* **Post within 15 minutes of a customer-visible incident, before the cause is known.** The first
  post says what is affected and when the next update comes. It never says why.
* **Update on the interval you promised**, even to say nothing has changed. A missed update is
  read as "they have given up".
* **The updates are trilingual** — Si, Ta, En (the platform's universal rule). Pre-write the
  templates so this is not a translation exercise at 3 a.m.
* **Never name a vendor as the cause while an incident is open.** "An upstream provider" until
  the postmortem is written and the vendor has confirmed it.
* **Resolve only after the alert has been clear for 15 minutes**, and post a resolution note.

### Maintenance

Scheduled work goes up 48 hours ahead with a window in Asia/Colombo time. The window is the
platform's own trough (02:00–04:00), which is the same hour the nightly dump and the daily-fee
reset run — check `docs/runbooks/deploy.md` before choosing one so three things do not collide.

## 6. Postmortems

Any page that woke somebody, any customer-visible incident, and any near miss that only did not
page because of luck.

Blameless, written within five working days, and it must answer one question the templates
usually miss: **what would have told us sooner?** Three of C130's twelve chaos drills produced no
signal at all while they were happening, and the postmortems that matter on this platform are the
ones that turn a silent failure into an alert.

A postmortem with no action item is a postmortem that concluded the system is fine. That is
sometimes true and it should be written down as a conclusion, not left as an absence.
