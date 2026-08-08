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
  'fleet.signIn.newAccount': 'இன்னும் கணக்கு இல்லையா?',
  'fleet.signIn.newAccountBody':
    'Fleet Portal கணக்கு உங்களுக்காக உருவாக்கப்படும் — உங்களை அழைக்கும் நிறுவனத்தின் உரிமையாளரால், அல்லது புதிய இயக்குநர் சேர்க்கப்படும்போது MageRide ஆல். உள்நுழைய முடிந்ததும், நிறுவனம் முதல் திரையில் அமைக்கப்படும்.',

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

  /* ---- The shell's placeholder ------------------------------------------ */
  'fleet.screen.pendingTitle': 'இந்தத் திரை இன்னும் உருவாக்கப்படவில்லை',
  'fleet.screen.pendingBody':
    'Fleet Portal கட்டமைப்பு இந்தப் பாதையைக் கண்டறிந்தது, உங்கள் பாத்திரமும் அதை அனுமதிக்கிறது. திரை பிற்கால கட்டமைப்புக் கூறுடன் வரும்.',
  'fleet.screen.servedBy': 'API வழங்குவது {service}',
  'fleet.screen.wireframe': 'கம்பி வரைவு {screen}',
};
