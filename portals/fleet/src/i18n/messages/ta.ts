import type { FleetMessages } from './en';

/**
 * Tamil resources for the Fleet Portal (D-26).
 *
 * Typed as {@link FleetMessages}, so a key added to `en.ts` and forgotten here is
 * a compile error rather than an English string on a Tamil operator's screen.
 * Product names — MageRide, Google, Apple, CSV — are left in Latin script, which
 * is how they are written in Tamil technical copy.
 */
export const fleetTa: FleetMessages = {
  /* ---- Shell chrome ---------------------------------------------------- */
  'fleet.appName': 'MageRide Fleet',
  'fleet.tagline': 'உங்கள் வாகனத் தொகுதியை நிர்வகியுங்கள்',
  'fleet.skipToContent': 'உள்ளடக்கத்திற்குச் செல்',
  'fleet.nav.label': 'தொகுதி பட்டி',
  'fleet.nav.open': 'பட்டியைத் திற',
  'fleet.nav.close': 'பட்டியை மூடு',
  'fleet.user.menu': 'உங்கள் கணக்கு',
  'fleet.user.signOut': 'வெளியேறு',
  'fleet.user.role': 'உங்கள் பாத்திரம்',
  'fleet.appearance.label': 'தோற்றம்',
  'fleet.appearance.light': 'வெளிர்',
  'fleet.appearance.dark': 'அடர்',
  'fleet.appearance.system': 'சாதனத்தின்படி',
  'fleet.language.label': 'மொழி',

  /* ---- The nav --------------------------------------------------------- */
  'fleet.nav.group.setup': 'அமைப்பு',
  'fleet.nav.group.operate': 'இயக்கம்',
  'fleet.nav.group.manage': 'நிர்வாகம்',
  'fleet.nav.group.subscribers': 'பயணிகள் (B முறை)',
  'fleet.nav.organisation': 'நிறுவனம்',
  'fleet.nav.payout': 'வங்கி & பணப்பட்டுவாடா',
  'fleet.nav.team': 'குழு',
  'fleet.nav.dashboard': 'கட்டுப்பாட்டுத் திரை',
  'fleet.nav.vehicles': 'வாகனங்கள்',
  'fleet.nav.drivers': 'ஓட்டுநர்கள்',
  'fleet.nav.trackers': 'ட்ராக்கர்கள்',
  'fleet.nav.map': 'நேரடி வரைபடம்',
  'fleet.nav.scheduling': 'கால அட்டவணை',
  'fleet.nav.analytics': 'பகுப்பாய்வு',
  'fleet.nav.billing': 'கட்டணப் பட்டியல்',
  'fleet.nav.subscriptions': 'சந்தாக்கள்',
  'fleet.nav.payments': 'கொடுப்பனவுகள்',

  /* ---- Org-scoped sub-roles -------------------------------------------- */
  'fleet.role.owner': 'உரிமையாளர்',
  'fleet.role.manager': 'மேலாளர்',
  'fleet.role.viewer': 'பார்வையாளர்',

  /* ---- Organisation status --------------------------------------------- */
  'fleet.status.pending': 'சரிபார்ப்பு நிலுவையில்',
  'fleet.status.approved': 'சரிபார்க்கப்பட்டது',
  'fleet.status.rejected': 'நிராகரிக்கப்பட்டது',

  /* ---- SCR-FP-001 · sign-in -------------------------------------------- */
  'fleet.signIn.heading': 'MageRide Fleet',
  'fleet.signIn.email': 'பணி மின்னஞ்சல்',
  'fleet.signIn.password': 'கடவுச்சொல்',
  'fleet.signIn.submit': 'உள்நுழை',
  'fleet.signIn.submitting': 'உள்நுழைகிறது…',
  'fleet.signIn.or': 'அல்லது இதன் மூலம்',
  'fleet.signIn.google': 'Google',
  'fleet.signIn.apple': 'Apple',
  'fleet.signIn.emailRequired': 'உங்கள் பணி மின்னஞ்சல் முகவரியை உள்ளிடவும்',
  'fleet.signIn.passwordRequired': 'உங்கள் கடவுச்சொல்லை உள்ளிடவும்',
  'fleet.signIn.signedOut': 'நீங்கள் வெளியேற்றப்பட்டுள்ளீர்கள்.',
  'fleet.signIn.noSecondFactor':
    'OTP அல்லது authenticator படி இல்லை — உள்நுழைந்ததும் நேராக உங்கள் தொகுதிக்குச் செல்வீர்கள்.',
  'fleet.signIn.forgot': 'கடவுச்சொல்லை மறந்துவிட்டீர்களா?',
  'fleet.signIn.forgotBody':
    'இந்தத் திரையிலிருந்து Fleet Portal கடவுச்சொல்லை இன்னும் மீட்டமைக்க முடியாது. உங்கள் நிறுவன உரிமையாளரிடம் கேளுங்கள், அல்லது MageRide ஆதரவைத் தொடர்பு கொள்ளுங்கள்; உங்களுக்குப் புதிய கடவுச்சொல் அமைக்கப்படும்.',

  /* ---- SCR-FP-001 · the sign-up half ----------------------------------- */
  'fleet.auth.tabs': 'Fleet Portal அணுகல்',
  'fleet.auth.tab.signIn': 'உள்நுழை',
  'fleet.auth.tab.signUp': 'கணக்கை உருவாக்கு',
  'fleet.signUp.title': 'Fleet Portal கணக்கு எப்படி உருவாகிறது',
  'fleet.signUp.unavailable':
    'இந்தத் திரையிலிருந்து புதிய Fleet Portal கணக்கைத் திறக்க முடியாது. ஒன்றைப் பெற இரண்டு வழிகள் உள்ளன, இரண்டுமே வேறு இடத்தில் தொடங்குகின்றன.',
  'fleet.signUp.byOwner':
    'உங்கள் நிறுவனம் ஏற்கெனவே MageRide-ஐப் பயன்படுத்தினால் — நிறுவன அமைப்புத் திரையிலிருந்து உங்கள் பணி மின்னஞ்சல் முகவரியைக் குழுவில் சேர்க்கும்படி அதன் உரிமையாளரிடம் கேளுங்கள். சேர்த்ததும் நீங்கள் உள்நுழையலாம்.',
  'fleet.signUp.byMageRide':
    'உங்கள் நிறுவனம் MageRide-க்குப் புதியதென்றால் — உங்களுக்கு இயக்குநர் கணக்கு திறக்க MageRide-ஐத் தொடர்பு கொள்ளுங்கள்.',
  'fleet.signUp.thenOrg':
    'உள்நுழைய முடிந்ததும், உங்களுக்குக் காட்டப்படும் முதல் திரையில் நிறுவனத்தைப் பதிவு செய்யுங்கள்.',
  'fleet.signUp.verification': 'மின்னஞ்சல் முகவரியைச் சரிபார்க்கிறீர்களா?',
  'fleet.signUp.verificationBody':
    'Fleet Portal கணக்குக்கு MageRide இன்னும் சரிபார்ப்பு மின்னஞ்சல் அனுப்புவதில்லை, சுய-சேவை கடவுச்சொல் மீட்டமைப்பும் இல்லை. உங்களைக் குழுவில் சேர்ப்பவரே உங்கள் முகவரியை உறுதிப்படுத்துகிறார்.',
  'fleet.signUp.identities': 'Google அல்லது Apple பயன்படுத்துகிறீர்களா?',
  'fleet.signUp.identitiesBody':
    'உங்கள் கணக்கு இருந்ததும், அதே பணி மின்னஞ்சல் முகவரியைப் பயன்படுத்தும் வரை Google-ம் Apple-ம் வேலை செய்யும். பிறகு ஒரு வழங்குநரை இணைப்பதோ நீக்குவதோ இன்னும் கிடைக்கவில்லை.',

  /* ---- Errors ---------------------------------------------------------- */
  'fleet.error.title': 'அது நிறைவேறவில்லை',
  'fleet.error.unauthorized': 'உங்கள் அமர்வு முடிந்தது. மீண்டும் உள்நுழையவும்.',
  'fleet.error.forbidden': 'உங்கள் பாத்திரம் அதற்கு அனுமதி அளிக்கவில்லை.',
  'fleet.error.notFound': 'அந்தப் பதிவு இனி இல்லை.',
  'fleet.error.validationFailed': 'குறிக்கப்பட்ட புலங்களைச் சரிபார்த்து மீண்டும் முயற்சிக்கவும்.',
  'fleet.error.conflict': 'வேறு ஒருவர் இதை முதலில் மாற்றிவிட்டார். பக்கத்தை மீண்டும் ஏற்றி முயற்சிக்கவும்.',
  'fleet.error.accountBlocked': 'இந்தக் கணக்கு தடுக்கப்பட்டுள்ளது. MageRide ஆதரவு அதை மீட்டெடுக்க முடியும்.',
  'fleet.error.invalidCredentials': 'அந்த மின்னஞ்சலும் கடவுச்சொல்லும் எந்தக் கணக்குடனும் பொருந்தவில்லை.',
  'fleet.error.accountLocked':
    'தோல்வியடைந்த உள்நுழைவு முயற்சிகள் மிக அதிகம். இந்தக் கணக்கு சிறிது நேரம் பூட்டப்பட்டுள்ளது.',
  'fleet.error.accountLockedFor':
    'தோல்வியடைந்த உள்நுழைவு முயற்சிகள் மிக அதிகம். சுமார் {minutes} நிமிடங்களில் மீண்டும் முயற்சிக்கவும்.',
  'fleet.error.rateLimited': 'கோரிக்கைகள் மிக அதிகம். சிறிது நேரம் காத்திருந்து மீண்டும் முயற்சிக்கவும்.',
  'fleet.error.serviceUnavailable': 'இப்போது MageRide-ஐ அணுக முடியவில்லை. சிறிது நேரத்தில் முயற்சிக்கவும்.',
  'fleet.error.unexpected': 'எங்கள் தரப்பில் ஏதோ தவறு நடந்தது.',
  'fleet.error.providerFailed':
    '{provider} உள்நுழைவு நிறைவடையவில்லை. மீண்டும் முயற்சிக்கவும், அல்லது கடவுச்சொல்லைப் பயன்படுத்தவும்.',
  'fleet.error.noFleetAccount':
    'இந்தக் கணக்கால் Fleet Portal-இல் உள்நுழைய முடியாது. உங்கள் மின்னஞ்சல் முகவரியை அழைக்கும்படி நிறுவன உரிமையாளரிடம் கேளுங்கள், அல்லது MageRide ஆதரவைத் தொடர்பு கொள்ளுங்கள்.',
  'fleet.error.orgNotFound': 'அந்த நிறுவனம் இனி இல்லை.',
  'fleet.error.notMember': 'நீங்கள் அந்த நிறுவனத்தின் உறுப்பினர் அல்ல.',
  'fleet.error.roleInsufficient': 'இந்த நிறுவனத்தில் உங்கள் பாத்திரம் அதற்கு அனுமதி அளிக்கவில்லை.',
  'fleet.error.orgNotApproved':
    'இந்த நிறுவனம் இன்னும் சரிபார்க்கப்பட்டு வருகிறது, எனவே அது இன்னும் கிடைக்கவில்லை.',
  'fleet.error.registrationExists':
    'அந்த வணிகப் பதிவு எண் ஏற்கெனவே MageRide-இல் பதிவு செய்யப்பட்டுள்ளது. அது உங்களுடையதாக இருக்க வேண்டுமானால் MageRide ஆதரவைத் தொடர்பு கொள்ளுங்கள்.',
  'fleet.error.memberExists': 'அந்த மின்னஞ்சல் முகவரிக்கு ஏற்கெனவே இந்த நிறுவனத்தில் இடம் உள்ளது.',
  'fleet.error.payoutNotFound': 'இந்த நிறுவனத்திற்கு இன்னும் வங்கி மற்றும் கொடுப்பனவு விவரங்கள் இல்லை.',
  'fleet.error.payoutNotVerified':
    'அதற்குச் சரிபார்க்கப்பட்ட வங்கி மற்றும் கொடுப்பனவுச் சுயவிவரம் தேவை. கணக்கு விவரங்களையும் ஆவணங்களையும் சேருங்கள்; சரிபார்ப்பு அதிகாரி அவற்றை அங்கீகரிப்பார்.',
  'fleet.error.fileTooLarge': 'அந்தக் கோப்பு {megabytes} MB-ஐ விடப் பெரியது. சிறிய நகலைப் பதிவேற்றவும்.',
  'fleet.error.fileNotAccepted': 'அந்த வகைக் கோப்பு இங்கே ஏற்கப்படவில்லை.',
  'fleet.error.vehicleRegistrationExists':
    'அந்த இலக்கத் தகட்டுடன் ஒரு வாகனம் ஏற்கெனவே MageRide இல் பதிவாகியுள்ளது. அது உங்களுக்கு விற்கப்பட்டிருந்தால், MageRide ஆதரவுக் குழு அதை மாற்றித் தரும்.',
  'fleet.error.invalidVehicleType': 'அது MageRide வாகன வகை அல்ல. பட்டியலிலிருந்து ஒன்றைத் தேர்ந்தெடுக்கவும்.',
  'fleet.error.modeNotAllowed':
    'ஒரு வாகனத் தொகுதி அட்டவணைப்படுத்தப்பட்ட மற்றும் பகிரப்பட்ட தனியார் வாகனங்களை இயக்குகிறது. ரயில்கள் MageRide ஆல் மையமாகப் பதிவு செய்யப்படுகின்றன.',
  'fleet.error.vehicleNotFound': 'அந்த வாகனம் உங்கள் தொகுதியில் இல்லை.',
  'fleet.error.driverNotFound':
    'அந்தப் பயனர் ID அல்லது கைபேசி எண்ணுக்குப் பொருந்தும் MageRide ஓட்டுநர் கணக்கு இல்லை. ஓட்டுநர் முதலில் Driver App இல் பதிவு செய்ய வேண்டும்.',
  'fleet.error.imeiDuplicate':
    'அந்த IMEI ஏற்கெனவே ஒரு வாகனத்துடன் இணைக்கப்பட்டுள்ளது. நிர்வாகி தீர்க்கும் வரை இரு சாதனங்களும் நிறுத்தி வைக்கப்பட்டுள்ளன, அதுவரை எதுவும் தரவு அனுப்பாது.',
  'fleet.error.csvInvalid': 'அந்தக் கோப்பை CSV ஆகப் படிக்க முடியவில்லை. நெடுவரிசைகளைச் சரிபார்த்து மீண்டும் பதிவேற்றவும்.',
  'fleet.error.tooManyRows': 'அந்தக் கோப்பில் மிக அதிக வரிசைகள் உள்ளன. பிரித்துப் பகுதிகளாகப் பதிவேற்றவும்.',
  'fleet.error.bulkInProgress':
    'இந்த நிறுவனத்திற்கு ஏற்கெனவே ஓர் இறக்குமதி நடக்கிறது. அது முடியும் வரை காத்திருந்து மீண்டும் முயலவும்.',
  'fleet.error.notOwner': 'அந்தச் சாதனம் வேறொரு நிறுவனத்திற்கு உரியது.',
  'fleet.error.attestationFailed':
    'தற்போது MageRide இந்தக் கோரிக்கையை Android மற்றும் iOS செயலிகளிலிருந்து மட்டுமே ஏற்கிறது. தொகுப்பை இயக்கித் தருமாறு MageRide ஆதரவுக் குழுவைக் கேளுங்கள்.',
  'fleet.error.reference': 'குறிப்பு: {traceId}',

  /* ---- Refusals and dead ends ------------------------------------------ */
  'fleet.denied.title': 'இந்தப் பக்கத்திற்கு உங்களுக்கு அணுகல் இல்லை',
  'fleet.denied.body':
    'இந்தத் திரை இந்த நிறுவனத்தில் உங்கள் பாத்திரத்தில் இல்லை. நீங்கள் அணுகக்கூடியதை உரிமையாளர் மாற்ற முடியும்.',
  'fleet.denied.back': 'உங்கள் முதல் திரைக்குச் செல்',
  'fleet.notFound.title': 'பக்கம் கிடைக்கவில்லை',
  'fleet.notFound.body': 'அந்த முகவரி எந்த Fleet Portal திரையுடனும் பொருந்தவில்லை.',
  'fleet.noScreens.title': 'இந்தக் கணக்கிற்கு இங்கு இன்னும் ஒன்றும் இல்லை',
  'fleet.noScreens.body':
    'நீங்கள் வெற்றிகரமாக உள்நுழைந்தீர்கள், ஆனால் இந்தக் கணக்கிற்கு எந்தத் தொகுதிப் பாத்திரமும் இல்லை. உங்கள் மின்னஞ்சல் முகவரியை மீண்டும் அழைக்கும்படி நிறுவன உரிமையாளரிடம் கேளுங்கள், அல்லது MageRide ஆதரவைத் தொடர்பு கொள்ளுங்கள்.',

  /* ---- No organisation yet --------------------------------------------- */
  'fleet.org.none.title': 'உங்கள் நிறுவனத்தை அமைக்கவும்',
  'fleet.org.none.body':
    'இந்தக் கணக்கு ஒரு நிறுவனத்தை உருவாக்க முடியும், ஆனால் இன்னும் எதற்கும் சொந்தமானது அல்ல. வாகனங்களையும் ஓட்டுநர்களையும் சேர்க்கத் தொடங்க அதைப் பதிவு செய்யுங்கள்.',

  /* ---- US-13.A7 · the verification gate --------------------------------- */
  'fleet.pending.title': 'உங்கள் நிறுவனம் இன்னும் சரிபார்க்கப்பட்டு வருகிறது',
  'fleet.pending.body':
    'MageRide சரிபார்ப்பு அதிகாரி உங்கள் பதிவையும் ஆவணங்களையும் பரிசீலித்து வருகிறார். அது அங்கீகரிக்கப்பட்டவுடன் வாகனச் சேர்க்கையும் ஓட்டுநர் ஒதுக்கீடும் திறக்கும்.',
  'fleet.pending.next':
    'காத்திருக்கும் வேளையில் நிறுவனச் சுயவிவரத்தை நிறைவு செய்யலாம், வங்கி மற்றும் பணப்பட்டுவாடா விவரங்களைச் சேர்க்கலாம், உங்கள் குழுவை அழைக்கலாம்.',
  'fleet.pending.blocked': 'வாகனங்களும் ஓட்டுநர் ஒதுக்கீடும் இன்னும் கிடைக்கவில்லை.',
  'fleet.rejected.title': 'உங்கள் நிறுவனம் அங்கீகரிக்கப்படவில்லை',
  'fleet.rejected.body':
    'சரிபார்ப்பு அதிகாரியால் இந்தப் பதிவை அங்கீகரிக்க முடியவில்லை. கீழே குறிப்பிட்டுள்ளதைச் சரிசெய்யுங்கள்; MageRide ஆதரவு பரிசீலனையை மீண்டும் திறக்கும்.',
  'fleet.rejected.reason': 'கூறப்பட்ட காரணம்: {reason}',
  'fleet.banner.pending':
    'இந்த நிறுவனம் சரிபார்ப்புக்குக் காத்திருக்கிறது. அங்கீகரிக்கப்படும் வரை அமைப்புத் திரைகள் மட்டுமே கிடைக்கும்.',
  'fleet.banner.rejected':
    'இந்த நிறுவனம் அங்கீகரிக்கப்படவில்லை. பரிசீலனையை மீண்டும் திறக்க MageRide ஆதரவைத் தொடர்பு கொள்ளுங்கள்.',
  'fleet.banner.viewer': 'நீங்கள் பார்வையாளராக உள்நுழைந்துள்ளீர்கள், எனவே இந்த அமர்வு படிக்க மட்டுமே.',

  /* ---- SCR-FP-002 · organisation setup --------------------------------- */
  'fleet.org.profile.heading': 'நிறுவனச் சுயவிவரம் மற்றும் KYC',
  'fleet.org.field.name': 'நிறுவனத்தின் பெயர்',
  'fleet.org.field.registrationNo': 'வணிகப் பதிவு எண்',
  'fleet.org.field.contactPhone': 'தொடர்பு கைபேசி',
  'fleet.org.field.contactEmail': 'தொடர்பு மின்னஞ்சல்',
  'fleet.org.field.address': 'முகவரி',
  'fleet.org.field.registered': 'MageRide-இல் பதிவு செய்யப்பட்டது',
  'fleet.org.field.language': 'மொழி',
  'fleet.org.hint.registrationNo': 'வணிகப் பதிவுச் சான்றிதழில் அச்சிடப்பட்டுள்ளபடியே.',
  'fleet.org.hint.contactPhone': 'இலங்கைக் கைபேசி எண், எடுத்துக்காட்டாக 0771234567.',
  'fleet.org.optional': 'விருப்பத்தேர்வு',
  'fleet.org.required': 'தேவை',
  'fleet.org.language.note':
    'இந்த உலாவியில் இந்தக் கன்சோலின் மொழியை அமைக்கிறது. நிறுவனத்திற்கென ஒரு மொழியை MageRide சேமிப்பதில்லை.',
  'fleet.org.readOnly':
    'இந்த விவரங்கள் சரிபார்ப்பு அதிகாரி படிக்கும் பதிவு; அவற்றை இந்த வலைவாயிலால் இன்னும் திருத்த முடியாது. இங்கு எதையேனும் திருத்த MageRide ஆதரவைத் தொடர்பு கொள்ளுங்கள்.',
  'fleet.org.kyc.heading': 'KYC மற்றும் சரிபார்ப்பு',
  'fleet.org.kyc.gate':
    'வாகனச் சேர்க்கையும் ஓட்டுநர் ஒதுக்கீடும் திறப்பதற்கு முன் MageRide சரிபார்ப்பு அதிகாரி பதிவைச் சரிபார்க்கிறார். அதுவரை படிக்கும் செயல்கள் மட்டுமே கிடைக்கும்.',
  'fleet.org.kyc.unavailable':
    'வணிகப் பதிவுச் சான்றிதழையும் உரிமையாளரின் அடையாள ஆவணத்தையும் MageRide ஆதரவு சேகரிக்கிறது; அவற்றைப் பதிவேற்ற வலைவாயிலில் இன்னும் இடம் இல்லை. கீழுள்ள வங்கி ஆவணங்கள் வங்கி மற்றும் கொடுப்பனவுச் சுயவிவரத்துடன் இணைக்கப்படுகின்றன.',
  'fleet.org.payout.link': 'வங்கி மற்றும் கொடுப்பனவு விவரங்கள்',
  'fleet.org.payout.linkBody':
    'B முறை சந்தாக் கட்டணங்கள் வரும் கணக்கு, மற்றும் உங்கள் பயணிகள் ஸ்கேன் செய்யும் வங்கிச் செயலி QR. உரிமையாளருக்கு மட்டும்.',
  'fleet.org.register.heading': 'உங்கள் நிறுவனத்தைப் பதிவு செய்யுங்கள்',
  'fleet.org.register.body':
    'இந்தக் கணக்கால் ஒரு நிறுவனத்தை உருவாக்க முடியும், ஆனால் இன்னும் எதற்கும் சொந்தமில்லை. வாகனங்களையும் ஓட்டுநர்களையும் சேர்க்கத் தொடங்க அதைப் பதிவு செய்யுங்கள்.',
  'fleet.org.register.gate':
    'நிறுவனம் சரிபார்ப்புக்குக் காத்திருக்கும் நிலையில் உருவாக்கப்படுகிறது. MageRide சரிபார்ப்பு அதிகாரி அதை மதிப்பாய்வு செய்கிறார்; அவர் அங்கீகரிக்கும் வரை படிக்கும் செயல்கள் மட்டுமே கிடைக்கும்.',
  'fleet.org.register.submit': 'நிறுவனத்தைப் பதிவு செய்',
  'fleet.org.register.submitting': 'பதிவு செய்கிறது…',
  'fleet.org.error.nameRequired': 'நிறுவனத்தின் பெயரை உள்ளிடவும்',
  'fleet.org.error.registrationRequired': 'வணிகப் பதிவு எண்ணை உள்ளிடவும்',
  'fleet.org.error.phoneInvalid': 'இலங்கைக் கைபேசி எண்ணை உள்ளிடவும், எடுத்துக்காட்டாக 0771234567',
  'fleet.org.error.emailInvalid': 'சரியான மின்னஞ்சல் முகவரியை உள்ளிடவும்',

  /* ---- SCR-FP-002 · the team -------------------------------------------- */
  'fleet.team.heading': 'குழு உறுப்பினர்கள்',
  'fleet.team.caption': 'இந்த நிறுவனத்திற்காக உள்நுழையக்கூடியவர்களும் அவர்களின் பாத்திரங்களும்',
  'fleet.team.column.member': 'உறுப்பினர்',
  'fleet.team.column.role': 'பாத்திரம்',
  'fleet.team.you': '(நீங்கள்)',
  'fleet.team.empty': 'இன்னும் குழு உறுப்பினர்கள் இல்லை.',
  'fleet.team.backToOrg': 'நிறுவன அமைப்புக்குத் திரும்பு',
  'fleet.team.invite.heading': 'குழு உறுப்பினரை அழைக்கவும்',
  'fleet.team.invite.email': 'பணி மின்னஞ்சல்',
  'fleet.team.invite.name': 'பெயர்',
  'fleet.team.invite.role': 'பாத்திரம்',
  'fleet.team.invite.submit': 'உறுப்பினரை அழை',
  'fleet.team.invite.submitting': 'அழைக்கிறது…',
  'fleet.team.invite.done': 'அந்த முகவரிக்கு இப்போது இந்த நிறுவனத்தில் இடம் உள்ளது.',
  'fleet.team.invite.noOwnerSeat':
    'இரண்டாவது உரிமையாளரை இங்கே சேர்க்க முடியாது — நிறுவனம் அதைப் பதிவு செய்தவருக்கே சொந்தம்.',
  'fleet.team.invite.noEmail':
    'MageRide இன்னும் அழைப்பு மின்னஞ்சல் அனுப்புவதில்லை. அவர்களின் முகவரி சேர்க்கப்பட்டதை உங்கள் சகாவிடம் சொல்லுங்கள் — பிறகு அவர்கள் அதைக் கொண்டு Google அல்லது Apple மூலம் உள்நுழையலாம், அல்லது கடவுச்சொல் அமைக்க MageRide ஆதரவைக் கேட்கலாம்.',
  'fleet.team.invite.ownerOnlyNotice':
    'குழு உறுப்பினர்களைச் சேர்க்கவோ மாற்றவோ நிறுவன உரிமையாளரால் மட்டுமே முடியும்.',
  'fleet.team.error.ownerOnly': 'குழு உறுப்பினரைச் சேர்க்க நிறுவன உரிமையாளரால் மட்டுமே முடியும்.',
  'fleet.team.error.roleRequired': 'மேலாளர் அல்லது பார்வையாளரைத் தேர்ந்தெடுக்கவும்',

  /* ---- SCR-FP-002a · bank & payout details ------------------------------ */
  'fleet.payout.title': 'வங்கி மற்றும் கொடுப்பனவு விவரங்கள்',
  'fleet.payout.heading': 'வங்கிக் கணக்கு — B முறை சந்தாக் கட்டணங்கள் இதில் வரும்',
  'fleet.payout.field.bank': 'வங்கி',
  'fleet.payout.field.bankPlaceholder': 'உங்கள் வங்கியைத் தேர்ந்தெடுக்கவும்',
  'fleet.payout.field.branch': 'கிளை',
  'fleet.payout.field.accountNo': 'கணக்கு எண்',
  'fleet.payout.field.holder': 'கணக்கு வைத்திருப்பவரின் பெயர்',
  'fleet.payout.holderHint':
    'நிறுவனத்தின் அல்லது உரிமையாளரின் KYC பெயருடன் பொருந்த வேண்டும். சரிபார்ப்பு அதிகாரி இரண்டையும் ஒப்பிடுவார்.',
  'fleet.payout.editWarning':
    'இந்த விவரங்களைச் சேமித்தால் அவை சரிபார்ப்புக்கு அனுப்பப்படும். வலதுபுறம் உள்ள ஆவணங்களுடன் அவை சரிபார்க்கப்படும்.',
  'fleet.payout.editVerifiedWarning':
    'இந்தச் சுயவிவரம் சரிபார்க்கப்பட்டுள்ளது. மாற்றத்தைச் சேமித்தால் புதிய விவரங்கள் சரிபார்ப்புக்கு அனுப்பப்படும்; அதிகாரி அந்த மாற்றத்தை அங்கீகரிக்கும் வரை உங்கள் B முறை சந்தாதாரர்கள் அங்கீகரிக்கப்பட்ட கணக்கிற்கே செலுத்துவார்கள்.',
  'fleet.payout.submit': 'வங்கி விவரங்களைச் சேமி',
  'fleet.payout.submitting': 'சேமிக்கிறது…',
  'fleet.payout.saved': 'சேமிக்கப்பட்டது. விவரங்கள் இப்போது சரிபார்ப்புக்குக் காத்திருக்கின்றன.',
  'fleet.payout.backToOrg': 'நிறுவன அமைப்புக்குத் திரும்பு',
  'fleet.payout.status.none': 'சமர்ப்பிக்கப்படவில்லை',
  'fleet.payout.status.pending': 'சரிபார்ப்புக்குக் காத்திருக்கிறது',
  'fleet.payout.status.verified': 'சரிபார்க்கப்பட்டது',
  'fleet.payout.status.rejected': 'நிராகரிக்கப்பட்டது',
  'fleet.payout.status.superseded': 'புதிய பதிப்பால் மாற்றப்பட்டது',
  'fleet.payout.rejectedReason': 'கூறப்பட்ட காரணம்: {reason}',
  'fleet.payout.verifiedOn': '{date} அன்று சரிபார்க்கப்பட்டது.',
  'fleet.payout.gate.heading': 'இந்தச் சுயவிவரத்தை எதிர்பார்ப்பவை',
  'fleet.payout.gate.paid':
    'இந்தச் சுயவிவரம் சரிபார்க்கப்படும் வரை B முறை வாகனத்தின் சேவைக் கட்டணத்தை "கட்டணம்" என அமைக்க முடியாது.',
  'fleet.payout.gate.paidReady':
    'B முறை வாகனங்களை மாதாந்தக் கட்டணத்துடன் சேவைக் கட்டணம் "கட்டணம்" என அமைக்கலாம்.',
  'fleet.payout.gate.billing':
    'அதிகாரி இந்த விவரங்களை அங்கீகரிக்கும் வரை கட்டணச் சந்தாக்களுக்கு பில்லிங் தொடங்காது, பயணிக் கட்டணத் தாளிலும் செலுத்த ஏதும் காட்டப்படாது.',
  'fleet.payout.gate.paySheetReady':
    'B முறை சந்தாதாரர்கள் பரிமாற்றத்திற்கு இந்தக் கணக்கு விவரங்களையும், LankaQR கட்டணத்திற்குக் கீழுள்ள QR படத்தையும் காண்பார்கள்.',
  'fleet.payout.proof.heading': 'கணக்குக்கான ஆதாரம்',
  'fleet.payout.proof.which': 'இது எந்த ஆவணம்?',
  'fleet.payout.proof.prompt': 'வங்கி அறிக்கை அல்லது பாஸ்புக் பக்கத்தைப் பதிவேற்றவும்',
  'fleet.payout.proof.hint': 'PDF அல்லது புகைப்படம், 8 MB வரை.',
  'fleet.payout.qr.heading': 'வங்கிச் செயலி LankaQR குறியீடு',
  'fleet.payout.qr.prompt': 'உங்கள் வங்கிச் செயலியிலிருந்து LankaQR குறியீட்டுப் படத்தைப் பதிவேற்றவும்',
  'fleet.payout.qr.hint': 'புகைப்படம் அல்லது திரைப்பிடிப்பு, 8 MB வரை.',
  'fleet.payout.qr.note':
    'பயணிக் கட்டணத் தாளில் B முறை சந்தாதாரர்கள் ஸ்கேன் செய்யவோ வங்கிச் செயலியில் திறக்கவோ காட்டப்படுகிறது. பரிமாற்றத்தில் செலுத்தும் பயணிகள் அதற்குப் பதிலாகச் சரிபார்க்கப்பட்ட கணக்கு விவரங்களைக் காண்பார்கள்.',
  'fleet.payout.kind.bankStatement': 'சமீபத்திய வங்கி அறிக்கை',
  'fleet.payout.kind.passbook': 'பாஸ்புக்கின் முதல் பக்கம்',
  'fleet.payout.kind.lankaqr': 'வங்கிச் செயலி LankaQR குறியீடு',
  'fleet.payout.doc.uploading': 'பதிவேற்றுகிறது…',
  'fleet.payout.doc.uploaded': 'பதிவேற்றப்பட்டது',
  'fleet.payout.doc.missing': 'பதிவேற்றப்படவில்லை',
  'fleet.payout.error.bankRequired': 'வங்கியைத் தேர்ந்தெடுக்கவும்',
  'fleet.payout.error.branchRequired': 'கிளையை உள்ளிடவும்',
  'fleet.payout.error.accountRequired': 'கணக்கு எண்ணை உள்ளிடவும்',
  'fleet.payout.error.holderRequired': 'கணக்கு வைத்திருப்பவரின் பெயரை உள்ளிடவும்',
  'fleet.payout.error.kindRequired': 'இது எந்த ஆவணம் எனத் தேர்ந்தெடுக்கவும்',
  'fleet.payout.error.fileRequired': 'பதிவேற்ற ஒரு கோப்பைத் தேர்ந்தெடுக்கவும்',
  'fleet.payout.error.profileFirst':
    'முதலில் வங்கி விவரங்களைச் சேமியுங்கள் — ஆவணம் இணைக்கப்படுவது கொடுப்பனவுச் சுயவிவரத்துடன், நிறுவனத்துடன் அல்ல.',

  /* ---- SCR-FP-004 · vehicle onboarding ---------------------------------- */
  'fleet.vehicles.title': 'வாகனப் பதிவு',
  'fleet.vehicles.modesOnly': 'A / B முறைகள் மட்டும்',
  'fleet.vehicles.modesOnlyNote':
    'ஒரு வாகனத் தொகுதி அட்டவணைப்படுத்தப்பட்ட பொதுப் போக்குவரத்தையும் (A முறை) பகிரப்பட்ட தனியார் வாகனங்களையும் (B முறை) இயக்குகிறது. கோரிக்கை வாடகை என்பது ஓட்டுநரின் சொந்த வாகனம், அது Driver App இல் பதிவு செய்யப்படுகிறது.',
  'fleet.vehicles.tabs': 'வாகனங்களைச் சேர்க்கும் முறை',
  'fleet.vehicles.tab.single': 'ஒரு வாகனம்',
  'fleet.vehicles.tab.bulk': 'மொத்த CSV',
  'fleet.vehicles.viewerNotice':
    'நீங்கள் பார்வையாளராக உள்நுழைந்துள்ளீர்கள், எனவே இத்திரை வாகனப் பட்டியலைக் காட்டுகிறது, எதையும் சேர்ப்பதில்லை.',

  'fleet.vehicles.add.heading': 'வாகனம் சேர்',
  'fleet.vehicles.field.plate': 'பதிவு எண்',
  'fleet.vehicles.field.plateHint': 'இலக்கத் தகட்டில் உள்ளபடியே — எடுத்துக்காட்டாக NB-4521.',
  'fleet.vehicles.field.type': 'வாகன வகை',
  'fleet.vehicles.field.mode': 'முறை',
  'fleet.vehicles.mode.a': 'A முறை — அட்டவணைப்படுத்தப்பட்ட பொதுப் போக்குவரத்து',
  'fleet.vehicles.mode.b': 'B முறை — பகிரப்பட்ட தனியார் வாகனம்',
  'fleet.vehicles.type.bus': 'பேருந்து',
  'fleet.vehicles.type.van': 'வேன்',
  'fleet.vehicles.type.mini_van': 'மினி வேன்',
  'fleet.vehicles.type.flex': 'ஃபிளெக்ஸ்',
  'fleet.vehicles.type.sedan': 'செடான்',
  'fleet.vehicles.type.three_wheeler': 'முச்சக்கர வண்டி',
  'fleet.vehicles.type.motorbike': 'மோட்டார் சைக்கிள்',
  'fleet.vehicles.type.truck': 'டிரக்',
  'fleet.vehicles.type.mini_truck': 'மினி டிரக்',
  'fleet.vehicles.type.noTrain': 'ரயில்கள் MageRide ஆல் மையமாகப் பதிவு செய்யப்படுகின்றன, இங்கே சேர்க்கப்படுவதில்லை.',
  'fleet.vehicles.add.submit': 'வாகனம் சேர்',
  'fleet.vehicles.add.submitting': 'சேர்க்கிறது…',
  'fleet.vehicles.add.added': '{plate} பட்டியலில் சேர்ந்து மறுஆய்வில் உள்ளது. அதன் ஆவணங்களைக் கீழே பதிவேற்றவும்.',

  'fleet.vehicles.field.servicePayment': 'சேவைக் கட்டணம்',
  'fleet.vehicles.field.servicePaymentHint':
    'B முறைக்கு மட்டும். அலுவலக அல்லது பணியாளர் போக்குவரத்து பயணிகளிடம் எதுவும் வசூலிப்பதில்லை, எனவே "இலவசம்"; மற்றவை இயல்பு மாதக் கட்டணத்துடன் "கட்டணம்".',
  'fleet.vehicles.servicePayment.free': 'இலவசம்',
  'fleet.vehicles.servicePayment.paid': 'கட்டணம்',
  'fleet.vehicles.servicePayment.freeOffice': 'இலவசம் (அலுவலகம்)',
  'fleet.vehicles.servicePayment.notSet': 'அமைக்கப்படவில்லை',
  'fleet.vehicles.servicePayment.notApplicable': '—',
  'fleet.vehicles.servicePayment.paidWithFare': 'கட்டணம் · மாதம் ரூ. {fare}',
  'fleet.vehicles.field.fare': 'இயல்பு மாதக் கட்டணம் (கட்டணம்)',
  'fleet.vehicles.field.fareHint':
    'ஒரு சந்தாதாரருக்கு மாதம் ரூபாய். சந்தாத் திரையில் ஒவ்வொரு சந்தாதாரருக்கும் மாற்றலாம்.',
  'fleet.vehicles.servicePayment.heading': 'சேவைக் கட்டணம்',
  'fleet.vehicles.servicePayment.save': 'சேவைக் கட்டணத்தைச் சேமி',
  'fleet.vehicles.servicePayment.saving': 'சேமிக்கிறது…',
  'fleet.vehicles.servicePayment.saved': 'சேமிக்கப்பட்டது.',
  'fleet.vehicles.servicePayment.modeANote':
    'சேவைக் கட்டணம் B முறை வாகனங்களுக்கு உரியது. A முறை வாகனத்திற்குச் சந்தாக் கட்டணம் இல்லை.',

  'fleet.vehicles.docs.heading': 'வாகன ஆவணங்கள்',
  'fleet.vehicles.docs.forVehicle': 'வாகன ஆவணங்கள் · {plate}',
  'fleet.vehicles.docs.chooseVehicle':
    'ஆவணம் ஒரு வாகனத்துடன் இணைக்கப்படுகிறது. மேலே வாகனத்தைச் சேர்க்கவும், அல்லது பட்டியலிலிருந்து ஒன்றைத் தேர்ந்தெடுக்கவும் — அதன் நான்கு இடங்கள் இங்கே திறக்கும்.',
  'fleet.vehicles.docs.extraction':
    'ஒவ்வொரு ஆவணமும் AI ஆல் படிக்கப்படுகிறது — பதிவுக்கு எதிராக இலக்கத் தகடு, காப்பீடு மற்றும் வருவாய் உரிமத்தின் காலாவதி, அனுமதி எண் மற்றும் வழி — ஒவ்வொன்றுக்கும் "சரிபார்க்கப்பட்டது / நிலுவை / இல்லை" என்ற குறியீடு உண்டு.',
  'fleet.vehicles.docs.approvalGate':
    'தேவையான ஆவணம் இல்லாத அல்லது நிலுவையில் உள்ள நிலையில் வாகனம் "அங்கீகரிக்கப்பட்டது" நிலையை அடைய முடியாது.',
  'fleet.vehicles.docs.blocked': 'காத்திருப்பது: {slots}.',
  'fleet.vehicles.docs.ready':
    'தேவையான அனைத்து ஆவணங்களும் சரிபார்க்கப்பட்டன. முடிவை எடுப்பது சரிபார்ப்பு அதிகாரி.',
  'fleet.vehicles.docs.backToRoster': 'முழுப் பட்டியலைக் காட்டு',
  'fleet.vehicles.doc.registration': 'பதிவு நகல் (CR புத்தகம்)',
  'fleet.vehicles.doc.registrationHint': 'இலக்கத் தகடு CR புத்தகத்துடன் ஒப்பிடப்படுகிறது.',
  'fleet.vehicles.doc.insurance': 'காப்பீட்டுச் சான்றிதழ்',
  'fleet.vehicles.doc.insuranceHint': 'காலாவதி தேதி சான்றிதழிலிருந்து படிக்கப்படுகிறது.',
  'fleet.vehicles.doc.revenueLicense': 'வருவாய் உரிமம்',
  'fleet.vehicles.doc.revenueLicenseHint': 'உரிம எண்ணும் காலாவதி தேதியும் அதிலிருந்து படிக்கப்படுகின்றன.',
  'fleet.vehicles.doc.routePermit': 'வழி அனுமதிப் பத்திரம்',
  'fleet.vehicles.doc.routePermitHint': 'அனுமதி எண்ணும் வழியும் அதிலிருந்து படிக்கப்படுகின்றன.',
  'fleet.vehicles.doc.upload': 'கோப்பை இங்கே இடவும் அல்லது ஒன்றைத் தேர்ந்தெடுக்கவும்',
  'fleet.vehicles.doc.accept': 'PDF அல்லது புகைப்படம், {megabytes} MB வரை.',
  'fleet.vehicles.slot.verified': 'சரிபார்க்கப்பட்டது',
  'fleet.vehicles.slot.pending': 'நிலுவையில்',
  'fleet.vehicles.slot.missing': 'இல்லை',
  'fleet.vehicles.slot.required': 'தேவை',
  'fleet.vehicles.slot.optional': 'இந்த வாகனத்திற்கு விருப்பத்தேர்வு',
  'fleet.vehicles.slot.permitModeA': 'A முறைக்குத் தேவை.',
  'fleet.vehicles.slot.expires': '{date} அன்று காலாவதியாகும்',
  'fleet.vehicles.slot.uploading': 'பதிவேற்றிப் படிக்கிறது…',
  'fleet.vehicles.slot.replace': 'புதிய கோப்பைப் பதிவேற்றினால் இது மாற்றப்படும்.',
  'fleet.vehicles.slot.extracted': 'படிக்கப்பட்ட தகவல்',
  'fleet.vehicles.slot.fieldPending': 'அதிகாரிக்குக் காத்திருக்கிறது',
  'fleet.vehicles.slot.fieldUnread': 'படிக்கப்படவில்லை',
  'fleet.vehicles.field.expiry': 'காலாவதி தேதி',
  'fleet.vehicles.field.expiryHint': 'விருப்பத்தேர்வு. படிப்பில் தேதி கிடைக்காவிட்டால் மட்டுமே பயன்படும்.',
  'fleet.vehicles.extract.reg_no_match': 'இலக்கத் தகடு CR புத்தகத்துடன் ஒத்துள்ளது',
  'fleet.vehicles.extract.plate_text': 'படிக்கப்பட்ட இலக்கத் தகடு',
  'fleet.vehicles.extract.insurance_expiry': 'காப்பீட்டுக் காலாவதி',
  'fleet.vehicles.extract.revenue_no': 'வருவாய் உரிம எண்',
  'fleet.vehicles.extract.revenue_expiry': 'வருவாய் உரிமக் காலாவதி',
  'fleet.vehicles.extract.permit_no': 'அனுமதி எண்',
  'fleet.vehicles.extract.permit_route': 'வழி',
  'fleet.vehicles.extract.permit_expiry': 'அனுமதிக் காலாவதி',

  'fleet.vehicles.bulk.heading': 'மொத்த CSV',
  'fleet.vehicles.bulk.prompt': 'CSV ஐ இங்கே இடவும் அல்லது கோப்பைத் தேர்ந்தெடுக்கவும்',
  'fleet.vehicles.bulk.hint': '{rows} வரிசைகள் வரை, {megabytes} MB.',
  'fleet.vehicles.bulk.columns': 'நெடுவரிசைகள்: {columns}. தலைப்பு வரிசை விருப்பத்தேர்வு.',
  'fleet.vehicles.bulk.docsPending':
    'இறக்குமதியாகும் ஒவ்வொரு வரிசையும் ஆவணங்கள் நிலுவையில் உள்ள நிலையில் உருவாக்கப்படும் — CSV இல் கோப்புகள் செல்லாது, எனவே நான்கு இடங்களும் பின்னர் வாகனவாரியாக நிரப்பப்பட வேண்டும்.',
  'fleet.vehicles.bulk.uploading': 'பதிவேற்றுகிறது…',
  'fleet.vehicles.bulk.processing': '{total} வரிசைகளை இறக்குமதி செய்கிறது…',
  'fleet.vehicles.bulk.imported': '{total} வரிசைகளில் {imported} இறக்குமதியாகின.',
  'fleet.vehicles.bulk.someFailed': '{failed} வரிசைகள் இறக்குமதியாகவில்லை.',
  'fleet.vehicles.bulk.allImported': 'அனைத்து வரிசைகளும் இறக்குமதியாகின.',
  'fleet.vehicles.bulk.report': 'பிழை அறிக்கையைப் பதிவிறக்கு',
  'fleet.vehicles.bulk.refresh': 'மீண்டும் பார்',
  'fleet.vehicles.bulk.jobFailed': 'அந்த இறக்குமதியைச் செயலாக்க முடியவில்லை. கோப்பைச் சரிபார்த்து மீண்டும் பதிவேற்றவும்.',

  'fleet.vehicles.table.heading': 'பதிவு நிலை',
  'fleet.vehicles.table.caption': 'இந்த நிறுவனத்தின் ஒவ்வொரு வாகனமும், அதன் ஆவணங்களும் அங்கீகார நிலையும்',
  'fleet.vehicles.column.plate': 'பதிவு எண்',
  'fleet.vehicles.column.type': 'வகை',
  'fleet.vehicles.column.servicePayment': 'சேவைக் கட்டணம்',
  'fleet.vehicles.column.documents': 'ஆவணங்கள்',
  'fleet.vehicles.column.status': 'நிலை',
  'fleet.vehicles.table.empty': 'இன்னும் வாகனங்கள் இல்லை. மேலே ஒன்றைச் சேர்க்கவும், அல்லது CSV ஐ இறக்குமதி செய்யவும்.',
  'fleet.vehicles.typeWithMode': '{type} ({mode})',
  'fleet.vehicles.docsCell.verified': '{required} இல் {verified} சரிபார்க்கப்பட்டன',
  'fleet.vehicles.docsCell.withPermit': '{required} இல் {verified} சரிபார்க்கப்பட்டன (வழி அனுமதி உட்பட)',
  'fleet.vehicles.docsCell.outstanding': '{verified}/{required} — {slot} {status}',
  'fleet.vehicles.docsCell.pending': 'ஆவணங்கள் நிலுவையில்',
  'fleet.vehicles.docsCell.complete': 'ஆவணங்கள் முழுமை',
  'fleet.vehicles.manage': 'ஆவணங்கள்',
  'fleet.vehicles.status.pending': 'மறுஆய்வில்',
  'fleet.vehicles.status.approved': 'அங்கீகரிக்கப்பட்டது',
  'fleet.vehicles.status.rejected': 'நிராகரிக்கப்பட்டது',
  'fleet.vehicles.status.deactivated': 'செயலிழக்கப்பட்டது',

  'fleet.vehicles.error.plateRequired': 'இலக்கத் தகட்டு எண்ணை உள்ளிடவும்',
  'fleet.vehicles.error.typeRequired': 'வாகன வகையைத் தேர்ந்தெடுக்கவும்',
  'fleet.vehicles.error.modeRequired': 'A முறை அல்லது B முறையைத் தேர்ந்தெடுக்கவும்',
  'fleet.vehicles.error.fareRequired': 'இயல்பு மாதக் கட்டணத்தை ரூபாயில் உள்ளிடவும்',
  'fleet.vehicles.error.servicePaymentRequired': '"இலவசம்" அல்லது "கட்டணம்" எனத் தேர்ந்தெடுக்கவும்',
  'fleet.vehicles.error.servicePaymentModeA':
    'சேவைக் கட்டணம் B முறை வாகனங்களுக்கு மட்டுமே. A முறை வாகனத்திற்கு அதை வெறுமையாக விடவும்.',
  'fleet.vehicles.error.vehicleRequired': 'முதலில் வாகனத்தைத் தேர்ந்தெடுக்கவும்',
  'fleet.vehicles.error.kindRequired': 'அது நான்கு ஆவண இடங்களில் ஒன்றல்ல',
  'fleet.vehicles.error.fileRequired': 'பதிவேற்ற ஒரு கோப்பைத் தேர்ந்தெடுக்கவும்',
  'fleet.vehicles.error.csvRequired': 'இறக்குமதி செய்ய CSV ஐத் தேர்ந்தெடுக்கவும்',
  'fleet.vehicles.error.csvTooLarge':
    'அந்தக் கோப்பு {megabytes} MB ஐ விடப் பெரியது. பிரித்துப் பகுதிகளாக இறக்குமதி செய்யவும்.',

  /* ---- SCR-FP-005 · driver assignment ----------------------------------- */
  'fleet.drivers.title': 'ஓட்டுநர் நியமனம்',
  'fleet.drivers.assign.heading': 'ஓட்டுநரை நியமி',
  'fleet.drivers.field.driver': 'பயனர் ID / தொலைபேசி மூலம் ஓட்டுநரை நியமி',
  'fleet.drivers.field.driverHint':
    'ஓட்டுநரின் MageRide பயனர் ID, அல்லது Driver App இல் அவர்கள் பயன்படுத்தும் கைபேசி எண் — எடுத்துக்காட்டாக 0771234567.',
  'fleet.drivers.field.vehicles': 'வாகனங்கள்',
  'fleet.drivers.field.vehiclesHint': 'ஒரு ஓட்டுநரை ஒரே சமயத்தில் பல வாகனங்களுக்கு நியமிக்கலாம்.',
  'fleet.drivers.field.from': 'முதல்',
  'fleet.drivers.field.fromHint': 'இப்போதே தொடங்க வெறுமையாக விடவும்.',
  'fleet.drivers.field.to': 'வரை',
  'fleet.drivers.field.toHint':
    'கால வரம்பற்ற நியமனத்திற்கு வெறுமையாக விடவும். இறுதித் தேதி அதைத் தற்காலிகமாக்கும், அது தானாகவே காலாவதியாகும்.',
  'fleet.drivers.assign.submit': 'நியமி',
  'fleet.drivers.assign.submitting': 'நியமிக்கிறது…',
  'fleet.drivers.assign.done': '{count} வாகனங்களுக்கு நியமிக்கப்பட்டது.',
  'fleet.drivers.assign.doneOne': 'நியமிக்கப்பட்டது.',
  'fleet.drivers.assign.refused': '{plate}: {reason}',
  'fleet.drivers.temporary':
    'தற்காலிகமாக அமர்த்தப்பட்ட ஓட்டுநர் இறுதித் தேதியுடன் நியமிக்கப்படுகிறார்; நியமனம் தானாகக் காலாவதியாகும், எதையும் ரத்து செய்யத் தேவையில்லை.',
  'fleet.drivers.noVehicles':
    'இன்னும் நியமிக்க வாகனங்கள் இல்லை. முதலில் வாகனத் திரையில் ஒன்றைப் பதிவு செய்யவும்.',
  'fleet.drivers.viewerNotice':
    'நீங்கள் பார்வையாளராக உள்நுழைந்துள்ளீர்கள், எனவே இத்திரை நியமனங்களைக் காட்டுகிறது, எதையும் மாற்றுவதில்லை.',

  'fleet.drivers.table.heading': 'நியமனங்கள்',
  'fleet.drivers.table.caption': 'இந்த நிறுவனத்தின் ஒவ்வொரு ஓட்டுநர் நியமனமும், செயலில் உள்ளவை முதலில்',
  'fleet.drivers.column.driver': 'ஓட்டுநர்',
  'fleet.drivers.column.vehicle': 'வாகனம்',
  'fleet.drivers.column.since': 'முதல்',
  'fleet.drivers.column.until': 'வரை',
  'fleet.drivers.column.status': 'நிலை',
  'fleet.drivers.column.actions': 'செயல்கள்',
  'fleet.drivers.table.empty': 'இன்னும் எந்த ஓட்டுநரும் நியமிக்கப்படவில்லை.',
  'fleet.drivers.openEnded': 'கால வரம்பற்றது',
  'fleet.drivers.status.active': 'செயலில்',
  'fleet.drivers.status.revoked': 'ரத்து செய்யப்பட்டது',
  'fleet.drivers.status.expired': 'முடிந்தது',
  'fleet.drivers.status.scheduled': 'பின்னர் தொடங்கும்',
  'fleet.drivers.revoke': 'ரத்து செய்',
  'fleet.drivers.revoking': 'ரத்து செய்கிறது…',
  'fleet.drivers.revokeNote':
    'ரத்து செய்தால் ஓட்டுநர் அந்த வாகனத்தில் புதிய அமர்வைத் தொடங்குவது உடனே நிற்கும்; ஏற்கெனவே நடக்கும் பயணம் முடிய அனுமதிக்கப்படும்.',
  'fleet.drivers.history': 'ரத்து செய்யப்பட்டவை, காலாவதியானவை உட்பட நியமன வரலாறு வாகனவாரியாகப் பேணப்படுகிறது.',
  'fleet.drivers.noInvite':
    'ஓட்டுநருக்கு ஏற்கெனவே MageRide Driver App கணக்கு இருக்க வேண்டும். இங்கிருந்து அவர்களுக்கு அழைப்பு அனுப்ப முடியாது — Driver App இல் பதிவு செய்யச் சொல்லி, பின்னர் அவர்களின் எண்ணால் நியமியுங்கள்.',
  'fleet.drivers.error.driverRequired':
    'ஓட்டுநரின் பயனர் ID அல்லது இலங்கைக் கைபேசி எண்ணை உள்ளிடவும், எடுத்துக்காட்டாக 0771234567',
  'fleet.drivers.error.vehicleRequired': 'குறைந்தது ஒரு வாகனத்தைத் தேர்ந்தெடுக்கவும்',
  'fleet.drivers.error.windowInverted': 'இறுதித் தேதி தொடக்கத் தேதிக்குப் பின் இருக்க வேண்டும்',
  'fleet.drivers.error.assignmentRequired': 'அந்த நியமனம் இனி இல்லை',

  /* ---- SCR-FP-006 · tracker binding ------------------------------------- */
  'fleet.trackers.title': 'ட்ராக்கர் இணைப்பு',
  'fleet.trackers.bind.heading': 'ட்ராக்கரை இணை',
  'fleet.trackers.autoSession': 'தானியங்கு அமர்வு அமைப்பு',
  'fleet.trackers.field.imei': 'IMEI / MAC',
  'fleet.trackers.field.imeiHint': 'ST-901 இல் அச்சிடப்பட்ட 15 இலக்கங்கள். இடைவெளிகளும் கோடுகளும் புறக்கணிக்கப்படும்.',
  'fleet.trackers.field.vehicle': 'வாகனம்',
  'fleet.trackers.field.autoStart': 'ட்ராக்கரிலிருந்தே பயணங்களைத் தொடங்கி முடி',
  'fleet.trackers.field.autoStartHint':
    'ஓட்டுநர் செயலியைத் திறந்தாலும் இல்லாவிட்டாலும் பேருந்து அதன் ட்ராக்கரிலிருந்து தரவை அனுப்பும், பயணம் இக்னிஷனுடன் தொடங்கி முடியும்.',
  'fleet.trackers.bind.submit': 'ட்ராக்கரை இணை',
  'fleet.trackers.bind.submitting': 'இணைக்கிறது…',
  'fleet.trackers.bind.done': '{imei} இணைக்கப்பட்டு அதன் சான்று வழங்கப்பட்டது.',
  'fleet.trackers.bind.pendingOrg':
    'சரிபார்ப்பு அதிகாரி இந்த நிறுவனத்தை அங்கீகரித்ததும் ட்ராக்கர் இணைப்பு திறக்கும்.',
  'fleet.trackers.noVehicles':
    'இன்னும் ட்ராக்கரை இணைக்க வாகனங்கள் இல்லை. முதலில் வாகனத் திரையில் ஒன்றைப் பதிவு செய்யவும்.',
  'fleet.trackers.viewerNotice':
    'நீங்கள் பார்வையாளராக உள்நுழைந்துள்ளீர்கள், எனவே இத்திரை ட்ராக்கர் நிலையைக் காட்டுகிறது, எதையும் இணைப்பதில்லை.',

  'fleet.trackers.bulk.heading': 'மொத்த இணைப்பு',
  'fleet.trackers.bulk.prompt': 'CSV ஐ இங்கே இடவும் அல்லது கோப்பைத் தேர்ந்தெடுக்கவும்',
  'fleet.trackers.bulk.hint': '{rows} வரிசைகள் வரை, {megabytes} MB.',
  'fleet.trackers.bulk.columns': 'நெடுவரிசைகள்: {columns}.',
  'fleet.trackers.bulk.credentialType': 'சான்று',
  'fleet.trackers.bulk.credential.x509': 'சான்றிதழ் (MQTT ட்ராக்கர்கள்)',
  'fleet.trackers.bulk.credential.psk': 'முன்-பகிர்ந்த சாவி (பழைய TCP ட்ராக்கர்கள்)',
  'fleet.trackers.bulk.credentialHint':
    'முழுத் தொகுப்புக்கும் ஒரே தேர்வு — ஒரு தொகுதி பொதுவாக ஒரே தலைமுறை வன்பொருள்.',
  'fleet.trackers.bulk.uploading': 'பதிவேற்றுகிறது…',
  'fleet.trackers.bulk.processing': '{total} ட்ராக்கர்களை இணைக்கிறது…',
  'fleet.trackers.bulk.bound': '{total} ட்ராக்கர்களில் {succeeded} இணைக்கப்பட்டன.',
  'fleet.trackers.bulk.someFailed': '{failed} வரிசைகள் இணைக்கப்படவில்லை.',
  'fleet.trackers.bulk.report': 'வரிசை அறிக்கையைப் பதிவிறக்கு',
  'fleet.trackers.bulk.refresh': 'மீண்டும் பார்',
  'fleet.trackers.bulk.jobFailed': 'அந்தத் தொகுப்பைச் செயலாக்க முடியவில்லை. கோப்பைச் சரிபார்த்து மீண்டும் முயலவும்.',

  'fleet.trackers.table.heading': 'ST-901 ட்ராக்கர்கள்',
  'fleet.trackers.table.caption':
    'இந்த நிறுவனத்துடன் இணைந்த ஒவ்வொரு ட்ராக்கரும், அதன் வாகனம், தரவு அனுப்பும் வேகம், நிலை',
  'fleet.trackers.column.imei': 'IMEI / MAC',
  'fleet.trackers.column.vehicle': 'வாகனம்',
  'fleet.trackers.column.cadence': 'அனுப்பும் வேகம்',
  'fleet.trackers.column.lastSeen': 'கடைசியாக',
  'fleet.trackers.column.health': 'நிலை',
  'fleet.trackers.column.credential': 'சான்று',
  'fleet.trackers.table.empty': 'இந்த நிறுவனத்தின் எந்த வாகனத்துடனும் இன்னும் ட்ராக்கர் இணைக்கப்படவில்லை.',
  'fleet.trackers.state.online': 'இணைப்பில்',
  'fleet.trackers.state.stale': 'பழையது',
  'fleet.trackers.state.offline': 'இணைப்பில் இல்லை',
  'fleet.trackers.state.decommissioned': 'நீக்கப்பட்டது',
  'fleet.trackers.credential.active': 'செயலில்',
  'fleet.trackers.credential.revoked': 'ரத்து செய்யப்பட்டது',
  'fleet.trackers.counts': 'இணைப்பில் {online} · பழையது {stale} · இணைப்பில் இல்லை {offline}',
  'fleet.trackers.thresholds':
    '{stale} நிமிடங்கள் சமிக்ஞை இல்லாவிட்டால் "பழையது"; {offline} நிமிடங்கள் இல்லாவிட்டால் "இணைப்பில் இல்லை".',
  'fleet.trackers.truncated':
    'இப்பட்டியல் வரம்பிடப்பட்டுள்ளது. மேலுள்ள எண்ணிக்கைகள் தொகுதியின் ஒவ்வொரு ட்ராக்கரையும் உள்ளடக்குகின்றன.',
  'fleet.trackers.asOf': '{time} நிலவரப்படி',
  'fleet.trackers.never': 'ஒருபோதும் இல்லை',
  'fleet.trackers.unknownVehicle': 'பட்டியலில் இல்லை',
  'fleet.trackers.cadence': 'நகரும்போது {moving} வி · நிற்கும்போது {stationary} வி',
  'fleet.trackers.cadenceNote':
    'இது ஒவ்வொரு A மற்றும் B முறை அமர்வும் தரவை அனுப்பும் வேகம். வாகனவாரியான வேகத்தை இன்னும் போர்ட்டலிலிருந்து அமைக்க முடியாது — மாற்றத்திற்கு MageRide ஆதரவுக் குழுவைக் கேளுங்கள்.',
  'fleet.trackers.error.imeiInvalid': 'ட்ராக்கரில் அச்சிடப்பட்ட 15 இலக்கங்களை உள்ளிடவும்',
  'fleet.trackers.error.vehicleRequired': 'இந்த ட்ராக்கர் பொருத்தப்பட்ட வாகனத்தைத் தேர்ந்தெடுக்கவும்',
  'fleet.trackers.error.csvRequired': 'இறக்குமதி செய்ய CSV ஐத் தேர்ந்தெடுக்கவும்',

  /* ---- The shell's placeholder ------------------------------------------ */
  'fleet.screen.pendingTitle': 'இந்தத் திரை இன்னும் உருவாக்கப்படவில்லை',
  'fleet.screen.pendingBody':
    'Fleet Portal கட்டமைப்பு இந்தப் பாதையைக் கண்டறிந்தது, உங்கள் பாத்திரமும் அதை அனுமதிக்கிறது. திரை பிற்கால கட்டமைப்புக் கூறுடன் வரும்.',
  'fleet.screen.servedBy': 'API வழங்குவது {service}',
  'fleet.screen.wireframe': 'கம்பி வரைவு {screen}',
};
