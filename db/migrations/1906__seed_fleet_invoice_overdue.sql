-- =====================================================================================
-- 1906 — seed: the US-13.10 fleet-invoice dunning template
-- Source: URD US-13.10 / US-13.10b · D2' SCR-FP-010 · D5' §14.4 (no row) · AL-03, D-26
--
-- C060 (fleet-billing-svc). One template key in three languages, and it exists because this
-- component built its producer.
--
-- ⚠ Spec gap — micro-change-set, raised in the C060 handoff.
--   C060's deliverable is "dunning / overdue signalling to the Fleet Portal and
--   notification-svc". The Fleet Portal half is a state (`billing.fleet_invoices.status =
--   'OVERDUE'`, migration 1108) and an event on `fleet.events`; the notification-svc half
--   needs a type and a body, and D5' §14.4's matrix has no row for either. 1902/1904 seed no
--   key for a fleet invoice at all — the whole of Epic 13's billing is missing from the
--   notification tables.
--   **D5' §14.4 should carry a FLEET_INVOICE_OVERDUE row.**
--
--   `LOW_BALANCE` is NOT this and must not be reused. That one is US-9.9's driver-wallet
--   warning, wallet-svc emits it edge-triggered on a *driver* account crossing a threshold,
--   and its seeded body talks about topping up before the next trip. A fleet whose invoice is
--   eight days old may have a perfectly healthy balance and simply not have paid.
--
--   Seeded together with the type in `NotificationCatalogue` and the dunning sweep in
--   fleet-billing-svc, because 1904's own header records the rule both halves follow: a key
--   no service resolves, or a type nothing sends, is what 1902 refused to create.
--
-- The four values are what the notice actually carries: the month billed, the amount owed in
-- rupees, the days it is past its term, and the organisation's name. No per-vehicle detail —
-- a push that listed twelve buses would be a screen, and the breakdown is one tap away on
-- SCR-FP-010.
-- =====================================================================================

INSERT INTO content.notification_templates (template_key, language, subject, body) VALUES

  ('fleet_invoice_overdue','en','Fleet invoice overdue',
   'The {{periodMonth}} invoice for {{fleetName}} is Rs {{amount}} and is {{daysOverdue}} day(s) overdue. Top up the fleet wallet to settle it.'),
  ('fleet_invoice_overdue','si','ඉන්වොයිසය කල් ඉකුත් වී ඇත',
   '{{fleetName}} සඳහා {{periodMonth}} ඉන්වොයිසය රු. {{amount}} වන අතර දින {{daysOverdue}}ක් කල් ඉකුත් වී ඇත. එය පියවීමට ඔබේ වාහන සමූහයේ පසුම්බිය ප්‍රතිපූර්ණය කරන්න.'),
  ('fleet_invoice_overdue','ta','விலைப்பட்டியல் தாமதமாகிவிட்டது',
   '{{fleetName}} இன் {{periodMonth}} விலைப்பட்டியல் ரூ. {{amount}} ஆகும், {{daysOverdue}} நாள்(கள்) தாமதமாகிவிட்டது. அதைத் தீர்க்க உங்கள் வாகனத் தொகுதிப் பணப்பையை நிரப்புங்கள்.')

ON CONFLICT (template_key, language, version) DO NOTHING;
