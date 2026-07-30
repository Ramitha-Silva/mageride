-- =====================================================================================
-- 1903 — seed: the first-run feature carousel, in Sinhala, Tamil and English
-- Source: ADD AL-28 · D5' BR-25.1 · URD US-1.2 / US-1.2a / US-24.1 · D2' SCR-PA-002 (passenger)
--         and SCR-DA/DI-002 (driver) · D-26
--
-- Owned by C045 (content-svc). Three slides per audience, which is what every one of those
-- documents says: "3-slide tutorial (US-1.2)" for the passenger and "3 auto-advancing /
-- swipeable slides with paged dots" for the driver.
--
-- **The topics are the specs', the words are not.** US-1.2a names the four driver themes —
-- vehicle onboarding, 15 s dispatch, Directional Travel, in-app wallet & daily fee — for three
-- slides, so dispatch and Directional Travel share the middle one: they are the same promise
-- (how work reaches you) and splitting them would leave the wallet off a screen the story puts
-- on it. The passenger side has no per-slide list anywhere; the three chosen are the three things
-- SCR-PA-004/008/010 actually do — the live map (US-7.1/7.9), an up-front fare (US-8.2c/8.4) and
-- the payment choice plus trip-share and SOS (US-8.10, US-11.x). No spec prints headline or body
-- copy for either screen, so this is **content for the Admin Portal to edit, not a spec value** —
-- which is the whole point of serving it from a table.
--
-- The Sinhala and Tamil strings deliberately reuse the vocabulary 1902's FAQ seed already
-- established (පසුම්බිය / பணப்பை for wallet, දෛනික ගාස්තුව / தினசரி கட்டணம் for the daily fee,
-- ගමන / பயணம் for a ride) so two screens do not name the same thing differently.
--
-- `illustration_ref` values are app-bundled asset keys, not URLs: AL-28 is "pure presentation —
-- no new API", the assets ship in the app bundle, and content-svc serves the reference so a
-- later CDN move is an UPDATE rather than a release. `Content:AssetBaseUrl` (unset by default)
-- is what turns these into absolute URLs if one is ever wanted.
-- =====================================================================================

INSERT INTO content.onboarding_slides (audience, slot, illustration_ref, title_by_lang, body_by_lang) VALUES

  -- ---------------------------------------------------------------------------------
  -- Driver — SCR-DA/DI-002 (US-1.2a). Onboarding · how rides reach you · the money.
  -- ---------------------------------------------------------------------------------
  ('driver', 1, 'onboarding/driver-vehicle',
   '{"en":"Onboard your vehicle in four steps",
     "si":"වාහනය පියවර හතරකින් ලියාපදිංචි කරන්න",
     "ta":"நான்கு படிகளில் வாகனத்தைப் பதிவு செய்யுங்கள்"}'::jsonb,
   '{"en":"Add the vehicle, then upload its insurance, revenue licence and photos. Documents are read automatically and most vehicles are approved at once.",
     "si":"වාහනය එක් කර, රක්ෂණය, ආදායම් බලපත්‍රය සහ ඡායාරූප උඩුගත කරන්න. ලේඛන ස්වයංක්‍රීයව කියවනු ලැබේ; බොහෝ වාහන වහාම අනුමත වේ.",
     "ta":"வாகனத்தைச் சேர்த்து, காப்பீடு, வருவாய் உரிமம் மற்றும் புகைப்படங்களைப் பதிவேற்றுங்கள். ஆவணங்கள் தானாகவே வாசிக்கப்படும்; பெரும்பாலான வாகனங்கள் உடனடியாக அனுமதிக்கப்படும்."}'::jsonb),

  ('driver', 2, 'onboarding/driver-dispatch',
   '{"en":"Ride requests in 15 seconds",
     "si":"ගමන් ඉල්ලීම් තත්පර 15කින්",
     "ta":"15 வினாடிகளில் பயண கோரிக்கைகள்"}'::jsonb,
   '{"en":"Nearby requests come to you one at a time, with 15 seconds to accept. Heading somewhere yourself? Turn on Directional Travel and see only the rides going your way.",
     "si":"අවට ගමන් ඉල්ලීම් එකින් එක ඔබ වෙත එන අතර, පිළිගැනීමට තත්පර 15ක් ඇත. ඔබත් යම් දෙසකට යනවාද? දිශානුගත ගමන් සක්‍රීය කර ඔබේ මාර්ගයේ ගමන් පමණක් බලන්න.",
     "ta":"அருகிலுள்ள கோரிக்கைகள் ஒன்றன் பின் ஒன்றாக வரும்; ஏற்றுக்கொள்ள 15 வினாடிகள். நீங்களும் ஒரு திசையில் செல்கிறீர்களா? திசைப் பயணத்தை இயக்கி உங்கள் வழியில் உள்ள பயணங்களை மட்டும் பாருங்கள்."}'::jsonb),

  ('driver', 3, 'onboarding/driver-wallet',
   '{"en":"One wallet, one daily fee",
     "si":"එක් පසුම්බියක්, එක් දෛනික ගාස්තුවක්",
     "ta":"ஒரு பணப்பை, ஒரு தினசரி கட்டணம்"}'::jsonb,
   '{"en":"Top up your wallet in the app and pay a single flat daily fee per vehicle. Your first trip of the day is free, and the fare of every ride is yours.",
     "si":"යෙදුමෙන් පසුම්බියට මුදල් ඇතුළත් කර, එක් වාහනයකට දිනකට එක් නියත ගාස්තුවක් ගෙවන්න. දිනයේ පළමු ගමන නොමිලේ; සෑම ගමනකම ගාස්තුව ඔබේ ය.",
     "ta":"செயலியில் பணப்பையை நிரப்பி, ஒரு வாகனத்திற்கு ஒரு நாளைக்கு ஒரே நிலையான கட்டணம் செலுத்துங்கள். அன்றைய முதல் பயணம் இலவசம்; ஒவ்வொரு பயணத்தின் கட்டணமும் உங்களுடையது."}'::jsonb),

  -- ---------------------------------------------------------------------------------
  -- Passenger — SCR-PA/PI-002 (US-1.2). What is moving · what it costs · how you pay.
  -- ---------------------------------------------------------------------------------
  ('passenger', 1, 'onboarding/passenger-map',
   '{"en":"See what is moving near you",
     "si":"ඔබ අවට ගමන් කරන දේ බලන්න",
     "ta":"உங்கள் அருகில் இயங்குவதைப் பாருங்கள்"}'::jsonb,
   '{"en":"Buses, three-wheelers and taxis on one live map. Tap a bus to follow its route, or a shared vehicle to ask its owner for access.",
     "si":"බස්, ත්‍රීරෝද රථ සහ කුලී රථ එකම සජීවී සිතියමක. මාර්ගය අනුගමනය කිරීමට බසයක් තට්ටු කරන්න, නොහොත් බෙදාගත් වාහනයක හිමිකරුගෙන් ප්‍රවේශය ඉල්ලන්න.",
     "ta":"பஸ்கள், முச்சக்கர வண்டிகள், டாக்சிகள் — அனைத்தும் ஒரே நேரலை வரைபடத்தில். வழியைப் பின்தொடர பஸ்ஸைத் தட்டுங்கள், அல்லது பங்கிடப்பட்ட வாகனத்தின் உரிமையாளரிடம் அனுமதி கேளுங்கள்."}'::jsonb),

  ('passenger', 2, 'onboarding/passenger-booking',
   '{"en":"Book with the fare up front",
     "si":"ගාස්තුව කලින් දැන ගමන වෙන්කරන්න",
     "ta":"கட்டணத்தை முன்பே அறிந்து முன்பதிவு செய்யுங்கள்"}'::jsonb,
   '{"en":"Set your pickup point and destination, choose a vehicle type, and see the estimated fare before you confirm. Nearby drivers are offered your ride in turn.",
     "si":"ගමන් ආරම්භක ස්ථානය සහ ගමනාන්තය සලකුණු කර, වාහන වර්ගයක් තෝරන්න; තහවුරු කිරීමට පෙර ඇස්තමේන්තුගත ගාස්තුව පෙනේ. අවට රියදුරන්ට ඔබේ ගමන පිළිවෙළින් ඉදිරිපත් වේ.",
     "ta":"புறப்படும் இடத்தையும் சேருமிடத்தையும் குறித்து, வாகன வகையைத் தேர்ந்தெடுங்கள்; உறுதிப்படுத்தும் முன் மதிப்பிடப்பட்ட கட்டணம் தெரியும். அருகிலுள்ள ஓட்டுநர்களுக்கு உங்கள் பயணம் முறையே வழங்கப்படும்."}'::jsonb),

  ('passenger', 3, 'onboarding/passenger-payment',
   '{"en":"Pay the way you like, and travel safely",
     "si":"ඔබට කැමති ලෙස ගෙවන්න, ආරක්ෂිතව ගමන් කරන්න",
     "ta":"உங்களுக்கு விருப்பமான முறையில் செலுத்துங்கள், பாதுகாப்பாகப் பயணியுங்கள்"}'::jsonb,
   '{"en":"Cash, wallet, card or LankaQR — choose when you book. Share your trip with someone you trust, and reach help from the SOS button at any time.",
     "si":"මුදල්, පසුම්බිය, කාඩ්පත හෝ LankaQR — වෙන්කිරීමේදී තෝරන්න. ඔබ විශ්වාස කරන කෙනෙකු සමඟ ගමන බෙදාගන්න, සහ SOS බොත්තමෙන් ඕනෑම වේලාවක උදව් ලබා ගන්න.",
     "ta":"பணம், பணப்பை, அட்டை அல்லது LankaQR — முன்பதிவின் போது தேர்ந்தெடுங்கள். நம்பிக்கையான ஒருவருடன் பயணத்தைப் பங்கிடுங்கள், SOS பொத்தானில் எப்போதும் உதவி பெறுங்கள்."}'::jsonb)

-- Seeds are admin-editable in production and a re-run must never revert an operator's wording
-- (db/CLAUDE.md). DO NOTHING, never DO UPDATE.
ON CONFLICT (audience, slot) DO NOTHING;
