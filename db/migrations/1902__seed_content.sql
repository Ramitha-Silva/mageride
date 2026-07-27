-- =====================================================================================
-- 1902 — seed: trilingual notification templates and FAQ
-- Source: server_db_schema.md §20 · D6' §7.3/§7.4/I-29.2 · URD US-16.1 · D-26
--
-- D-26 and the root CLAUDE.md rule: no user-facing string is hardcoded and every one
-- exists in Sinhala, Tamil and English. These seeds are the day-0 set that makes the rule
-- true from the first migration rather than from the first admin edit.
--
-- Only template keys the specs actually name are seeded:
--   ride_offer           server_db_schema §20
--   package_on_the_way   D6' I-29.2 (existing SMS on driver pickup-confirm, P-09)
--   proxy_ride_link      D6' I-29.2 (new, AL-44 — scope proxy_rider, US-8.22/10.10)
--   pickup_confirm_link  D6' I-29.2 (new, AL-44 — scope pickup_confirm, TTL 300 s)
-- Inventing further keys here would put strings in the database that no service resolves.
-- =====================================================================================

INSERT INTO content.notification_templates (template_key, language, subject, body) VALUES
  -- Dispatch offer push (E-01, high priority). The only one of the four with a subject —
  -- the rest are SMS, which has no title.
  ('ride_offer','en','New ride request','New ride request: {{pickup}} → {{dropoff}}'),
  ('ride_offer','si','නව ගමන් ඉල්ලීමක්','නව ගමන් ඉල්ලීමක්: {{pickup}} → {{dropoff}}'),
  ('ride_offer','ta','புதிய பயண கோரிக்கை','புதிய பயண கோரிக்கை: {{pickup}} → {{dropoff}}'),

  -- P-09: SMS to an unregistered package recipient, carrying a package_recipient token.
  ('package_on_the_way','en',NULL,
   'Your package is on the way. Track it here: {{link}}'),
  ('package_on_the_way','si',NULL,
   'ඔබේ පාර්සලය මාර්ගයේ ය. මෙතැනින් නිරීක්ෂණය කරන්න: {{link}}'),
  ('package_on_the_way','ta',NULL,
   'உங்கள் பொதி வழியில் உள்ளது. இங்கே கண்காணிக்கவும்: {{link}}'),

  -- AL-44: SMS to a proxy rider when a driver accepts the ride booked for them.
  ('proxy_ride_link','en',NULL,
   'A ride has been booked for you. Follow it here: {{link}}'),
  ('proxy_ride_link','si',NULL,
   'ඔබ වෙනුවෙන් ගමනක් වෙන්කර ඇත. මෙතැනින් අනුගමනය කරන්න: {{link}}'),
  ('proxy_ride_link','ta',NULL,
   'உங்களுக்காக ஒரு பயணம் முன்பதிவு செய்யப்பட்டுள்ளது. இங்கே பின்தொடரவும்: {{link}}'),

  -- AL-44: SMS asking an unregistered rider to confirm their pickup point. 300 s TTL.
  ('pickup_confirm_link','en',NULL,
   'Confirm your pickup location: {{link}} — the link expires in 5 minutes.'),
  ('pickup_confirm_link','si',NULL,
   'ඔබේ ගමන් ආරම්භක ස්ථානය තහවුරු කරන්න: {{link}} — සබැඳිය මිනිත්තු 5කින් කල් ඉකුත් වේ.'),
  ('pickup_confirm_link','ta',NULL,
   'உங்கள் புறப்படும் இடத்தை உறுதிப்படுத்தவும்: {{link}} — இணைப்பு 5 நிமிடங்களில் காலாவதியாகும்.')
ON CONFLICT (template_key, language, version) DO NOTHING;

-- -------------------------------------------------------------------------------------
-- FAQ (US-16.1). The four topics are the ones the story names: wallet top-up, daily fee,
-- vehicle registration and ride booking. content.faq_articles has a generated UUID PK, so
-- re-runnability is a NOT EXISTS guard on (category, language) rather than ON CONFLICT.
-- -------------------------------------------------------------------------------------
INSERT INTO content.faq_articles (category, language, title, body, sort_order)
  SELECT v.category, v.language, v.title, v.body, v.sort_order
    FROM (VALUES
      ('wallet','en',0,'How do I top up my wallet?',
       'Open Wallet in the app and choose Top up. You can pay with an OnePay card, your OnePay wallet, or LankaQR. You can also buy a bulk credit voucher, which credits your wallet immediately at a discount.'),
      ('wallet','si',0,'මගේ පසුම්බියට මුදල් ඇතුළත් කරන්නේ කෙසේද?',
       'යෙදුමේ පසුම්බිය විවෘත කර මුදල් ඇතුළත් කිරීම තෝරන්න. OnePay කාඩ්පතක්, OnePay පසුම්බිය හෝ LankaQR මගින් ගෙවිය හැක. තොග ණය වවුචරයක් මිලදී ගැනීමෙන් වට්ටමක් සහිතව ඔබේ පසුම්බියට වහාම මුදල් එකතු වේ.'),
      ('wallet','ta',0,'எனது பணப்பையை எப்படி நிரப்புவது?',
       'செயலியில் பணப்பையைத் திறந்து நிரப்பு என்பதைத் தேர்ந்தெடுக்கவும். OnePay அட்டை, OnePay பணப்பை அல்லது LankaQR மூலம் செலுத்தலாம். மொத்தக் கடன் வவுச்சர் வாங்கினால், தள்ளுபடியுடன் உடனடியாக உங்கள் பணப்பையில் வரவு வைக்கப்படும்.'),

      ('daily_fee','en',1,'How is the daily fee charged?',
       'The daily fee is a single flat charge per vehicle per day, set by vehicle type. Your first trip of the day is free — the fee is taken from your wallet before your second trip. Mode A vehicles pay no daily fee.'),
      ('daily_fee','si',1,'දෛනික ගාස්තුව අය කරන්නේ කෙසේද?',
       'දෛනික ගාස්තුව යනු වාහන වර්ගය අනුව නියම වන, එක් වාහනයකට දිනකට එක් වරක් පමණක් අය කරන ගාස්තුවකි. දිනයේ පළමු ගමන නොමිලේ — ගාස්තුව ඔබේ දෙවන ගමනට පෙර පසුම්බියෙන් අඩු වේ. A ආකාරයේ වාහන සඳහා දෛනික ගාස්තුවක් නැත.'),
      ('daily_fee','ta',1,'தினசரி கட்டணம் எவ்வாறு விதிக்கப்படுகிறது?',
       'தினசரி கட்டணம் என்பது வாகன வகையைப் பொறுத்து, ஒரு வாகனத்திற்கு ஒரு நாளைக்கு ஒரு முறை மட்டும் விதிக்கப்படும் கட்டணம். அன்றைய முதல் பயணம் இலவசம் — இரண்டாவது பயணத்திற்கு முன் கட்டணம் உங்கள் பணப்பையிலிருந்து கழிக்கப்படும். A முறை வாகனங்களுக்கு தினசரி கட்டணம் இல்லை.'),

      ('vehicle_registration','en',2,'How do I register a vehicle?',
       'Add the vehicle in the Driver app and upload its registration, insurance and revenue licence, along with the vehicle photos. Documents are read automatically; anything unclear goes to a verification officer. The vehicle can go online once every document is verified.'),
      ('vehicle_registration','si',2,'වාහනයක් ලියාපදිංචි කරන්නේ කෙසේද?',
       'රියදුරු යෙදුමෙන් වාහනය එක් කර, ලියාපදිංචි සහතිකය, රක්ෂණය සහ ආදායම් බලපත්‍රය සමඟ වාහනයේ ඡායාරූප උඩුගත කරන්න. ලේඛන ස්වයංක්‍රීයව කියවනු ලැබේ; පැහැදිලි නොවන ඒවා සත්‍යාපන නිලධාරියෙකු වෙත යොමු වේ. සියලු ලේඛන සත්‍යාපනය වූ පසු වාහනය සේවයට යෙදිය හැක.'),
      ('vehicle_registration','ta',2,'வாகனத்தை எவ்வாறு பதிவு செய்வது?',
       'ஓட்டுநர் செயலியில் வாகனத்தைச் சேர்த்து, பதிவுச் சான்றிதழ், காப்பீடு, வருவாய் உரிமம் மற்றும் வாகனப் புகைப்படங்களைப் பதிவேற்றவும். ஆவணங்கள் தானாகவே வாசிக்கப்படும்; தெளிவில்லாதவை சரிபார்ப்பு அதிகாரிக்கு அனுப்பப்படும். எல்லா ஆவணங்களும் சரிபார்க்கப்பட்ட பின் வாகனத்தைச் சேவையில் இயக்கலாம்.'),

      ('booking','en',3,'How do I book a ride?',
       'Set your pickup point and destination on the map, choose a vehicle type, and confirm. The estimated fare is shown before you book. Nearby drivers are offered the ride in turn, and you can follow your ride on the map once one accepts.'),
      ('booking','si',3,'ගමනක් වෙන්කරන්නේ කෙසේද?',
       'සිතියමේ ඔබේ ගමන් ආරම්භය සහ ගමනාන්තය සලකුණු කර, වාහන වර්ගයක් තෝරා තහවුරු කරන්න. වෙන්කිරීමට පෙර ඇස්තමේන්තුගත ගාස්තුව පෙන්වයි. අවට රියදුරන්ට පිළිවෙළින් ගමන ඉදිරිපත් වන අතර, කෙනෙකු එය පිළිගත් පසු ඔබට සිතියමේ ගමන අනුගමනය කළ හැක.'),
      ('booking','ta',3,'பயணத்தை எவ்வாறு முன்பதிவு செய்வது?',
       'வரைபடத்தில் உங்கள் புறப்படும் இடத்தையும் சேருமிடத்தையும் குறித்து, வாகன வகையைத் தேர்ந்தெடுத்து உறுதிப்படுத்தவும். முன்பதிவுக்கு முன் மதிப்பிடப்பட்ட கட்டணம் காட்டப்படும். அருகிலுள்ள ஓட்டுநர்களுக்கு முறையே பயணம் வழங்கப்படும்; ஒருவர் ஏற்றுக்கொண்ட பின் வரைபடத்தில் பயணத்தைப் பின்தொடரலாம்.')
    ) AS v(category, language, sort_order, title, body)
   WHERE NOT EXISTS (
     SELECT 1 FROM content.faq_articles f
      WHERE f.category = v.category AND f.language = v.language);
