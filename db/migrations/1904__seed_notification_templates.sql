-- =====================================================================================
-- 1904 — seed: the trilingual notification bodies notification-svc renders
-- Source: D5' §14.4 (the per-type notification table) · D6' §7.3/§7.4 · D-26 ·
--         US-2.14, US-3.16, US-6A.15, US-9.9, US-10.7/10.9/10.12/10.13/10.14, US-20.5
--
-- 1902 seeded the four keys the specs name by string (`ride_offer`, `package_on_the_way`,
-- `proxy_ride_link`, `pickup_confirm_link`) and stopped there, deliberately: "inventing further
-- keys here would put strings in the database that no service resolves". C051 is the service
-- that resolves them, so the rest of §14.4's rows land here, beside the code that sends them —
-- the rule content-svc's CLAUDE.md states as "a new key ships in a migration beside the code
-- that sends it".
--
-- Every key is present in all three languages, which is not a convention here but a constraint:
-- 1307's `trg_notification_templates_trilingual` counts the languages per (key, version) at
-- COMMIT and rejects the whole transaction otherwise.
--
-- Two rules shaped the wording, and both are worth stating because they look like omissions:
--
--   (1) **A placeholder exists only where a producer fills it.** The events these are rendered
--       from carry ids, amounts and counts — never display strings. `ride.accepted` has a
--       `driverId` and no driver name; `package.picked_up` has no ETA. So `driver_assigned`
--       says "a driver has accepted your ride" rather than AL-21's "{driver} · ETA {n} min",
--       and the app fetches the detail behind the deep link. Filling those two would need
--       `ride.accepted` and `package.picked_up` to carry a name and an ETA — raised as a
--       micro-change-set in the C051 handoff, together with §20's `ride_offer`, whose
--       `{{pickup}}`/`{{dropoff}}` no producer on the platform can fill either.
--
--   (2) **No interpolated noun that would arrive in one language.** `document_expiring` says
--       "one of your documents" rather than "your {{document}}", because the value available is
--       `registry.documents.kind` — `insurance`, `revenue_licence` — an English identifier that
--       would be pasted verbatim into the Sinhala and Tamil bodies and quietly break D-26 for
--       the one sentence that matters. The deep link takes the driver to the document.
--
-- Not seeded, and named rather than invented: the AL-47 driver-QR prompt and its +5 min nudge,
-- US-8.15's refund notice and the COD reminders. Their producer (fare-svc) does not call this
-- service yet — C050's handoff says so — and a key no service resolves is exactly what 1902
-- refused to create.
-- =====================================================================================

INSERT INTO content.notification_templates (template_key, language, subject, body) VALUES

  -- E-01's other half. The offer *push* is a silent, high-priority data message (D5' §14.4
  -- "FCM-hi / APNs silent"), which is why it renders no body at all; this is what goes out over
  -- SMS when three seconds pass with no ack. Its two values are the ones `offer.created`
  -- actually carries — the fare and the distance to the pickup — because an SMS a driver cannot
  -- act on is worse than none.
  ('ride_offer_sms','en',NULL,
   'New MageRide request — Rs {{fare}}, pickup {{distance}} km away. Open the app to accept.'),
  ('ride_offer_sms','si',NULL,
   'නව MageRide ගමන් ඉල්ලීමක් — රු. {{fare}}, ආරම්භය කි.මී. {{distance}}ක් දුරින්. පිළිගැනීමට යෙදුම විවෘත කරන්න.'),
  ('ride_offer_sms','ta',NULL,
   'புதிய MageRide பயணக் கோரிக்கை — ரூ. {{fare}}, புறப்படும் இடம் {{distance}} கி.மீ. தொலைவில். ஏற்க செயலியைத் திறக்கவும்.'),

  -- D-33. The one message on the platform sent through both gateways at once, so it is also the
  -- one whose wording has to survive being read on a feature phone: who, and where.
  ('sos_alert','en',NULL,
   'MageRide emergency: {{name}} has raised an SOS. Live location: {{link}}'),
  ('sos_alert','si',NULL,
   'MageRide හදිසි අවස්ථාව: {{name}} SOS ඉල්ලීමක් යවා ඇත. සජීවී ස්ථානය: {{link}}'),
  ('sos_alert','ta',NULL,
   'MageRide அவசரநிலை: {{name}} SOS அனுப்பியுள்ளார். நேரடி இருப்பிடம்: {{link}}'),

  -- §14.4 DRIVER_ASSIGNED (US-10.8).
  ('driver_assigned','en','Driver on the way',
   'A driver has accepted your ride and is on the way to the pickup point.'),
  ('driver_assigned','si','රියදුරු පැමිණෙමින්',
   'රියදුරෙකු ඔබේ ගමන පිළිගෙන ආරම්භක ස්ථානය වෙත පැමිණෙමින් සිටී.'),
  ('driver_assigned','ta','ஓட்டுநர் வருகிறார்',
   'ஒரு ஓட்டுநர் உங்கள் பயணத்தை ஏற்று புறப்படும் இடத்தை நோக்கி வருகிறார்.'),

  -- §14.4 DRIVER_ARRIVED.
  ('driver_arrived','en','Driver has arrived',
   'Your driver is waiting at the pickup point.'),
  ('driver_arrived','si','රියදුරු පැමිණ ඇත',
   'ඔබේ රියදුරු ආරම්භක ස්ථානයේ රැඳී සිටී.'),
  ('driver_arrived','ta','ஓட்டுநர் வந்துவிட்டார்',
   'உங்கள் ஓட்டுநர் புறப்படும் இடத்தில் காத்திருக்கிறார்.'),

  -- §14.4 RIDE_CANCELLED (US-10.8). Safety-critical: US-10.7 will not let it be muted.
  ('ride_cancelled','en','Ride cancelled',
   'This ride has been cancelled.'),
  ('ride_cancelled','si','ගමන අවලංගු කර ඇත',
   'මෙම ගමන අවලංගු කර ඇත.'),
  ('ride_cancelled','ta','பயணம் ரத்து செய்யப்பட்டது',
   'இந்தப் பயணம் ரத்து செய்யப்பட்டுள்ளது.'),

  -- §14.4 PAYMENT_CONFIRMED (US-8.15).
  ('payment_confirmed','en','Payment received',
   'Your payment of Rs {{amount}} has been received.'),
  ('payment_confirmed','si','ගෙවීම ලැබී ඇත',
   'ඔබේ රු. {{amount}} ගෙවීම ලැබී ඇත.'),
  ('payment_confirmed','ta','கட்டணம் பெறப்பட்டது',
   'உங்கள் ரூ. {{amount}} கட்டணம் பெறப்பட்டது.'),

  -- §14.4 SCHEDULED_REMINDER — 30 min to the driver (US-6A.15), 1 h + 15 min to the passenger
  -- (US-10.9). One key for all three: the difference is when it is sent, not what it says.
  ('scheduled_reminder','en','Scheduled ride',
   'Your scheduled ride starts in {{minutes}} minutes.'),
  ('scheduled_reminder','si','නියමිත ගමන',
   'ඔබේ නියමිත ගමන මිනිත්තු {{minutes}}කින් ආරම්භ වේ.'),
  ('scheduled_reminder','ta','திட்டமிட்ட பயணம்',
   'உங்கள் திட்டமிட்ட பயணம் {{minutes}} நிமிடங்களில் தொடங்கும்.'),

  -- §14.4 DIRECTIONAL_EXPIRING — DT-08 / US-10.14's 10-minute pre-expiry reminder.
  ('directional_expiring','en','Destination Filter ending',
   'Your Destination Filter expires in {{minutes}} minutes.'),
  ('directional_expiring','si','ගමනාන්ත පෙරහන අවසන් වෙමින්',
   'ඔබේ ගමනාන්ත පෙරහන මිනිත්තු {{minutes}}කින් කල් ඉකුත් වේ.'),
  ('directional_expiring','ta','சேருமிட வடிகட்டி முடிவடைகிறது',
   'உங்கள் சேருமிட வடிகட்டி {{minutes}} நிமிடங்களில் காலாவதியாகும்.'),

  -- DT-04's counterpart (US-6A.21). The driver's banner comes down and the pool widens again;
  -- a driver who is not told assumes the filter is still holding rides back.
  ('directional_cleared','en','Destination Filter ended',
   'Your Destination Filter is no longer active. You will be offered all nearby rides again.'),
  ('directional_cleared','si','ගමනාන්ත පෙරහන අවසන් විය',
   'ඔබේ ගමනාන්ත පෙරහන තවදුරටත් ක්‍රියාත්මක නොවේ. අවට සියලු ගමන් නැවත ඔබට ඉදිරිපත් වේ.'),
  ('directional_cleared','ta','சேருமிட வடிகட்டி முடிந்தது',
   'உங்கள் சேருமிட வடிகட்டி இனி செயலில் இல்லை. அருகிலுள்ள அனைத்துப் பயணங்களும் மீண்டும் உங்களுக்கு வழங்கப்படும்.'),

  -- §14.4 LOW_BALANCE — US-9.9, once below the threshold.
  ('low_balance','en','Low wallet balance',
   'Your wallet balance is Rs {{balance}}. Top up to keep accepting rides.'),
  ('low_balance','si','පසුම්බියේ ශේෂය අඩුයි',
   'ඔබේ පසුම්බියේ ශේෂය රු. {{balance}}ය. ගමන් පිළිගැනීම දිගටම කරගෙන යාමට මුදල් ඇතුළත් කරන්න.'),
  ('low_balance','ta','பணப்பை இருப்பு குறைவு',
   'உங்கள் பணப்பை இருப்பு ரூ. {{balance}}. பயணங்களைத் தொடர்ந்து ஏற்க பணப்பையை நிரப்பவும்.'),

  -- D5' §9.4's second clause: below zero the wallet is not low, it is empty, and the driver
  -- cannot take a second trip until it is topped up. A different message, not a louder one.
  ('top_up_required','en','Top up required',
   'Your wallet balance is Rs {{balance}}. Top up before your next trip.'),
  ('top_up_required','si','මුදල් ඇතුළත් කිරීම අවශ්‍යයි',
   'ඔබේ පසුම්බියේ ශේෂය රු. {{balance}}ය. ඊළඟ ගමනට පෙර මුදල් ඇතුළත් කරන්න.'),
  ('top_up_required','ta','பணப்பையை நிரப்ப வேண்டும்',
   'உங்கள் பணப்பை இருப்பு ரூ. {{balance}}. அடுத்த பயணத்திற்கு முன் நிரப்பவும்.'),

  -- D-13 / US-9.1: the once-a-day deduction, announced because it is the one charge a driver
  -- does not initiate.
  ('daily_fee_charged','en','Daily fee charged',
   'The daily platform fee of Rs {{amount}} has been deducted from your wallet.'),
  ('daily_fee_charged','si','දෛනික ගාස්තුව අය කර ඇත',
   'රු. {{amount}} දෛනික වේදිකා ගාස්තුව ඔබේ පසුම්බියෙන් අඩු කර ඇත.'),
  ('daily_fee_charged','ta','தினசரி கட்டணம் விதிக்கப்பட்டது',
   'ரூ. {{amount}} தினசரி தளக் கட்டணம் உங்கள் பணப்பையிலிருந்து கழிக்கப்பட்டுள்ளது.'),

  -- E-03 at T−30 d / T−7 d / T−1 d. See rule (2) above for why the document is not named.
  ('document_expiring','en','Document expiring',
   'One of your documents expires in {{days}} days. Upload a renewal to stay online.'),
  ('document_expiring','si','ලේඛනයක් කල් ඉකුත් වෙමින්',
   'ඔබේ ලේඛනයක් දින {{days}}කින් කල් ඉකුත් වේ. සේවයේ රැඳී සිටීමට අලුත් කළ පිටපතක් උඩුගත කරන්න.'),
  ('document_expiring','ta','ஆவணம் காலாவதியாகிறது',
   'உங்கள் ஆவணங்களில் ஒன்று {{days}} நாட்களில் காலாவதியாகும். சேவையில் தொடர புதுப்பிக்கப்பட்ட ஆவணத்தைப் பதிவேற்றவும்.'),

  ('document_expired','en','Document expired',
   'A document has expired and this vehicle is suspended until it is renewed.'),
  ('document_expired','si','ලේඛනය කල් ඉකුත් වී ඇත',
   'ලේඛනයක් කල් ඉකුත් වී ඇති අතර, එය අලුත් කරන තෙක් මෙම වාහනය අත්හිටුවා ඇත.'),
  ('document_expired','ta','ஆவணம் காலாவதியானது',
   'ஒரு ஆவணம் காலாவதியாகிவிட்டது; அது புதுப்பிக்கப்படும் வரை இந்த வாகனம் இடைநிறுத்தப்பட்டுள்ளது.'),

  -- §14.4 REGISTRATION_RESULT (US-2.14), in its two outcomes. AL-27's auto-approval and
  -- AL-29/AL-30's "an officer has to look at this" are different facts to the driver waiting.
  ('registration_approved','en','Vehicle approved',
   'Your vehicle has been approved. You can go online and start accepting rides.'),
  ('registration_approved','si','වාහනය අනුමත විය',
   'ඔබේ වාහනය අනුමත කර ඇත. ඔබට සේවයට එක්වී ගමන් පිළිගැනීම ආරම්භ කළ හැක.'),
  ('registration_approved','ta','வாகனம் அங்கீகரிக்கப்பட்டது',
   'உங்கள் வாகனம் அங்கீகரிக்கப்பட்டது. நீங்கள் சேவையில் இணைந்து பயணங்களை ஏற்கத் தொடங்கலாம்.'),

  ('registration_review_required','en','Verification needed',
   'Some of the details you submitted need a verification officer to check them. We will let you know as soon as that is done.'),
  ('registration_review_required','si','සත්‍යාපනය අවශ්‍යයි',
   'ඔබ ඉදිරිපත් කළ තොරතුරු කිහිපයක් සත්‍යාපන නිලධාරියෙකු විසින් පරීක්ෂා කළ යුතුය. එය අවසන් වූ විගස ඔබට දැනුම් දෙන්නෙමු.'),
  ('registration_review_required','ta','சரிபார்ப்பு தேவை',
   'நீங்கள் சமர்ப்பித்த சில விவரங்களைச் சரிபார்ப்பு அதிகாரி பரிசோதிக்க வேண்டும். அது முடிந்தவுடன் உங்களுக்குத் தெரிவிப்போம்.'),

  -- AL-21's registered branch (US-20.5, P-09). The unregistered branch is 1902's
  -- `package_on_the_way`, which carries the share-token link instead of a deep link.
  ('package_picked_up','en','📦 Package on the way',
   'Your package has been picked up and is on the way.'),
  ('package_picked_up','si','📦 පාර්සලය මාර්ගයේ ය',
   'ඔබේ පාර්සලය රැගෙන ගොස් මාර්ගයේ ය.'),
  ('package_picked_up','ta','📦 பொதி வழியில் உள்ளது',
   'உங்கள் பொதி எடுக்கப்பட்டு வழியில் உள்ளது.'),

  -- US-10.13.
  ('package_delivered','en','Package delivered',
   'Your package has been delivered.'),
  ('package_delivered','si','පාර්සලය භාර දී ඇත',
   'ඔබේ පාර්සලය භාර දී ඇත.'),
  ('package_delivered','ta','பொதி வழங்கப்பட்டது',
   'உங்கள் பொதி வழங்கப்பட்டுவிட்டது.')

ON CONFLICT (template_key, language, version) DO NOTHING;
