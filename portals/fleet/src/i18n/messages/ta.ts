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

  /* ---- The shell's placeholder ------------------------------------------ */
  'fleet.screen.pendingTitle': 'இந்தத் திரை இன்னும் உருவாக்கப்படவில்லை',
  'fleet.screen.pendingBody':
    'Fleet Portal கட்டமைப்பு இந்தப் பாதையைக் கண்டறிந்தது, உங்கள் பாத்திரமும் அதை அனுமதிக்கிறது. திரை பிற்கால கட்டமைப்புக் கூறுடன் வரும்.',
  'fleet.screen.servedBy': 'API வழங்குவது {service}',
  'fleet.screen.wireframe': 'கம்பி வரைவு {screen}',
};
