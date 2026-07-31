-- =====================================================================================
-- 1905 — seed: the US-13.11 schedule-not-started alarm template
-- Source: URD US-13.11 / US-13.11b · D2' SCR-FP-008 · D5' §14.4 (no row) · D-26
--
-- C059 (fleet-svc-fleet-ops). One template key in three languages, and it exists because
-- this component built its producer.
--
-- ⚠ Spec gap — micro-change-set, raised in the C059 handoff.
--   US-13.11 promises "a ringing alarm in the Android and iOS Driver Apps (assigned driver)
--   and a Fleet Portal notification" when a scheduled journey has not started by its time,
--   and US-13.11b gives the driver "the vehicle, route, and scheduled time" in it. D5'
--   §14.4's notification matrix has no row for any of that, and
--   `content.notification_templates` (1902, 1904) seeds no key for it.
--   **D5' §14.4 should carry a SCHEDULE_NOT_STARTED row.**
--
--   `SCHEDULED_REMINDER` is NOT this and must not be reused. That one is dispatch-svc's
--   courtesy *before* a booking — US-6A.15's 30-minute driver reminder and US-10.9's
--   1 h + 15 min passenger one — and its seeded body says a ride is coming up. Sent to a
--   driver whose bus is already ten minutes late it would be actively wrong.
--
--   Seeded together with the type in `NotificationCatalogue` and the sweep in fleet-svc's
--   `ScheduleAlarmWorker`, because 1904's own header records the rule both halves follow:
--   a key no service resolves, or a type nothing sends, is what 1902 refused to create.
--
-- The three values are what the alarm actually carries: the plate the driver walks up to,
-- the departure time they missed, and the minutes since. No route name — `route_id` is
-- optional on a fleet schedule and `spatial.routes.name` is not translated, so a route that
-- had one would put an English string inside a Sinhala sentence, which is the D-26 failure
-- 1904's own header warns about for document kinds.
-- =====================================================================================

INSERT INTO content.notification_templates (template_key, language, subject, body) VALUES

  ('schedule_not_started','en','Journey not started',
   'Vehicle {{registrationNumber}} has not started its {{departAt}} journey. Start it now, or tell your operator.'),
  ('schedule_not_started','si','ගමන ආරම්භ කර නැත',
   '{{registrationNumber}} වාහනය එහි {{departAt}} ගමන ආරම්භ කර නොමැත. දැන් ආරම්භ කරන්න, නැතහොත් ඔබේ ක්‍රියාකරුට දන්වන්න.'),
  ('schedule_not_started','ta','பயணம் தொடங்கவில்லை',
   '{{registrationNumber}} வாகனம் அதன் {{departAt}} பயணத்தைத் தொடங்கவில்லை. இப்போது தொடங்குங்கள், அல்லது உங்கள் இயக்குநருக்குத் தெரிவியுங்கள்.')

ON CONFLICT (template_key, language, version) DO NOTHING;
