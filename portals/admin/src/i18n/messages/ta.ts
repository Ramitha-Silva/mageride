/** Tamil (ta) resources for the Admin Portal. Typed against `AdminMessages`, so it cannot fall behind `en.ts`. */

import type { AdminMessages } from './en';

export const adminTa: AdminMessages = {
  /* ---- Shell chrome ---------------------------------------------------- */
  'admin.appName': 'MageRide நிர்வாகம்',
  'admin.tagline': 'உள் பணியாளர்களுக்கு மட்டும்',
  'admin.skipToContent': 'உள்ளடக்கத்திற்குச் செல்',
  'admin.nav.label': 'தொகுதிகள்',
  'admin.nav.open': 'பட்டியலைத் திற',
  'admin.nav.close': 'பட்டியலை மூடு',
  'admin.user.menu': 'உங்கள் கணக்கு',
  'admin.user.signOut': 'வெளியேறு',
  'admin.user.roles': 'பாத்திரங்கள்',
  'admin.appearance.label': 'தோற்றம்',
  'admin.appearance.light': 'வெளிர்',
  'admin.appearance.dark': 'இருள்',
  'admin.appearance.system': 'என் சாதனத்தின்படி',
  'admin.language.label': 'மொழி',

  /* ---- SCR-AP-001 · sign-in -------------------------------------------- */
  'admin.signIn.heading': 'MageRide நிர்வாகம்',
  'admin.signIn.email': 'பணி மின்னஞ்சல்',
  'admin.signIn.password': 'கடவுச்சொல்',
  'admin.signIn.submit': 'உள்நுழை',
  'admin.signIn.submitting': 'உள்நுழைகிறது…',
  'admin.signIn.or': 'அல்லது',
  'admin.signIn.google': 'Google மூலம் உள்நுழை',
  'admin.signIn.noSecondFactor':
    'OTP அல்லது authenticator படி இல்லை — உள்நுழைந்ததும் நேராக உங்கள் பணிக்குச் செல்வீர்கள்.',
  'admin.signIn.emailRequired': 'உங்கள் பணி மின்னஞ்சல் முகவரியை உள்ளிடவும்',
  'admin.signIn.passwordRequired': 'உங்கள் கடவுச்சொல்லை உள்ளிடவும்',
  'admin.signIn.signedOut': 'நீங்கள் வெளியேற்றப்பட்டுள்ளீர்கள்.',
  'admin.signIn.forgot': 'கடவுச்சொல்லை மறந்துவிட்டீர்களா?',
  'admin.signIn.forgotBody':
    'உள்ளக கணக்குகளை உருவாக்குவதும் மீட்டமைப்பதும் ஒரு Super Admin — இங்கு சுய-சேவை மீட்டமைப்பு இல்லை. உங்களுக்குப் புதிய கடவுச்சொல்லை அமைக்கும்படி அவரிடம் கேளுங்கள்.',

  /* ---- Errors ---------------------------------------------------------- */
  'admin.error.title': 'அது நிறைவேறவில்லை',
  'admin.error.unauthorized': 'உங்கள் அமர்வு முடிந்தது. மீண்டும் உள்நுழையவும்.',
  'admin.error.forbidden': 'உங்கள் பாத்திரம் அதற்கு அனுமதி அளிக்கவில்லை.',
  'admin.error.notFound': 'அந்தப் பதிவு இனி இல்லை.',
  'admin.error.validationFailed': 'குறிக்கப்பட்ட புலங்களைச் சரிபார்த்து மீண்டும் முயற்சிக்கவும்.',
  'admin.error.conflict': 'வேறு ஒருவர் இதை முதலில் மாற்றிவிட்டார். பக்கத்தை மீண்டும் ஏற்றி முயற்சிக்கவும்.',
  'admin.error.accountBlocked': 'இந்தக் கணக்கு தடுக்கப்பட்டுள்ளது. முதன்மை நிர்வாகி அதை மீட்டெடுக்க முடியும்.',
  'admin.error.invalidCredentials': 'அந்த மின்னஞ்சலும் கடவுச்சொல்லும் எந்தக் கணக்குடனும் பொருந்தவில்லை.',
  'admin.error.accountLocked':
    'தோல்வியடைந்த உள்நுழைவு முயற்சிகள் மிக அதிகம். இந்தக் கணக்கு சிறிது நேரம் பூட்டப்பட்டுள்ளது.',
  'admin.error.accountLockedFor':
    'தோல்வியடைந்த உள்நுழைவு முயற்சிகள் மிக அதிகம். சுமார் {minutes} நிமிடங்களில் மீண்டும் முயற்சிக்கவும்.',
  'admin.error.rateLimited': 'கோரிக்கைகள் மிக அதிகம். சிறிது நேரம் காத்திருந்து மீண்டும் முயற்சிக்கவும்.',
  'admin.error.serviceUnavailable': 'இப்போது MageRide-ஐ அணுக முடியவில்லை. சிறிது நேரத்தில் முயற்சிக்கவும்.',
  'admin.error.unexpected': 'எங்கள் தரப்பில் ஏதோ தவறு நடந்தது.',
  'admin.error.googleFailed':
    'Google உள்நுழைவு நிறைவடையவில்லை. மீண்டும் முயற்சிக்கவும், அல்லது கடவுச்சொல்லைப் பயன்படுத்தவும்.',
  'admin.error.reference': 'குறிப்பு: {traceId}',

  /* ---- Refusals and dead ends ------------------------------------------ */
  'admin.denied.title': 'இந்தப் பக்கத்திற்கு உங்களுக்கு அணுகல் இல்லை',
  'admin.denied.body':
    'இந்தத் தொகுதி உங்கள் பாத்திரத்தில் இல்லை. உங்கள் பணிக்கு அது தேவைப்பட்டால் முதன்மை நிர்வாகியிடம் கேட்கவும்.',
  'admin.denied.back': 'உங்கள் முதல் தொகுதிக்குச் செல்',
  'admin.notFound.title': 'பக்கம் கிடைக்கவில்லை',
  'admin.notFound.body': 'அந்த முகவரி எந்த நிர்வாகப் போர்ட்டல் திரையுடனும் பொருந்தவில்லை.',
  'admin.noModules.title': 'உங்களுக்கு இன்னும் தொகுதிகள் ஒதுக்கப்படவில்லை',
  'admin.noModules.body':
    'உங்கள் கணக்கு வெற்றிகரமாக உள்நுழைந்தது, ஆனால் உங்கள் பாத்திரங்கள் எந்த நிர்வாகப் போர்ட்டல் திரையையும் திறக்கவில்லை. உங்களுக்குத் தேவையானதை வழங்கும்படி முதன்மை நிர்வாகியிடம் கேட்கவும்.',

  /* ---- The shell's placeholder for a screen a later component owns ------ */
  'admin.screen.pendingTitle': 'இந்தத் திரை இன்னும் உருவாக்கப்படவில்லை',
  'admin.screen.pendingBody':
    'நிர்வாகப் போர்ட்டல் கட்டமைப்பு இந்தப் பாதையைக் கண்டறிந்தது, உங்கள் பாத்திரமும் அதை அனுமதிக்கிறது. திரை பிற்கால கட்டமைப்புக் கூறுடன் வரும்.',
  'admin.screen.servedBy': 'API வழங்குவது {service}',

  /* ---- SCR-AP-002 · dashboard and its statistics filter (AL-38) -------- */
  'admin.dashboard.filter.legend': 'புள்ளிவிவரக் காலம்',
  'admin.dashboard.filter.today': 'இன்று',
  'admin.dashboard.filter.week': 'இந்த வாரம்',
  'admin.dashboard.filter.month': 'இந்த மாதம்',
  'admin.dashboard.filter.custom': 'தெரிவு செய்த காலம்',
  'admin.dashboard.filter.from': 'முதல்',
  'admin.dashboard.filter.to': 'வரை',
  'admin.dashboard.filter.apply': 'பயன்படுத்து',
  'admin.dashboard.filter.comparison': 'முந்தைய காலத்துடன் ஒப்பிடும்போது',
  'admin.dashboard.filter.export': 'CSV ஆகப் பதிவிறக்கு',
  'admin.dashboard.filter.chooseRange': 'அந்தக் காலத்தின் புள்ளிகளைப் பார்க்க இரு முனைகளையும் தேர்வு செய்யுங்கள்.',
  'admin.dashboard.filter.timezone': 'திகதிகள் இலங்கை நேரப்படி (Asia/Colombo).',

  'admin.dashboard.period.heading': 'தேர்ந்தெடுத்த காலத்திற்கு',
  'admin.dashboard.live.heading': 'இப்போது',
  'admin.dashboard.live.note': 'நேரடி எண்ணிக்கைகள். இம்மூன்றும் கால வடிகட்டியால் மாறுவதில்லை.',

  'admin.dashboard.kpi.completedTrips': 'நிறைவடைந்த பயணங்கள்',
  'admin.dashboard.kpi.grossFare': 'மொத்தக் கட்டணம்',
  'admin.dashboard.kpi.newRidersDrivers': 'புதிய பயணிகள் / ஓட்டுநர்கள்',
  'admin.dashboard.kpi.newRiders': 'புதிய பயணிகள்',
  'admin.dashboard.kpi.newDrivers': 'புதிய ஓட்டுநர்கள்',
  'admin.dashboard.kpi.riders': 'பயணிகள்',
  'admin.dashboard.kpi.drivers': 'ஓட்டுநர்கள்',
  'admin.dashboard.kpi.dailyFeeRevenue': 'தினசரிக் கட்டண வருவாய்',
  'admin.dashboard.kpi.onlineDrivers': 'இணைப்பில் உள்ள ஓட்டுநர்கள்',
  'admin.dashboard.kpi.pendingVerifications': 'சரிபார்க்க நிலுவையில்',
  'admin.dashboard.kpi.openTickets': 'திறந்த டிக்கெட்டுகள்',

  'admin.dashboard.delta.up': '{metric}: முந்தைய காலத்தை விட {value} அதிகம்',
  'admin.dashboard.delta.down': '{metric}: முந்தைய காலத்தை விட {value} குறைவு',
  'admin.dashboard.delta.flat': '{metric}: முந்தைய காலத்தைப் போலவே',
  'admin.dashboard.delta.unknown': '{metric}: ஒப்பீடு இல்லை, முந்தைய காலத்தில் எதுவும் இல்லை',

  'admin.dashboard.money': 'ரூ. {amount}',

  'admin.dashboard.alerts.heading': 'கவனம் தேவை',
  'admin.dashboard.alerts.clear': 'இப்போது உங்களுக்காக எதுவும் காத்திருக்கவில்லை.',
  'admin.dashboard.alerts.verification': 'சரிபார்ப்புக்குக் காத்திருக்கும் சமர்ப்பிப்புகள்',
  'admin.dashboard.alerts.tickets': 'இன்னும் திறந்திருக்கும் ஆதரவு டிக்கெட்டுகள்',
  'admin.dashboard.alerts.count': '{count} காத்திருக்கிறது',

  /* ---- D-35 ------------------------------------------------------------ */
  'admin.audit.notice': 'இந்தச் செயல் உங்கள் பெயருடன் தணிக்கைப் பதிவில் எழுதப்படும்.',
  'admin.audit.recorded': '{action} என தணிக்கைப் பதிவில் பதிவு செய்யப்பட்டது.',

  /* ---- The nine canonical roles (AL-06) -------------------------------- */
  'admin.role.admin': 'நிர்வாகி',
  'admin.role.super_admin': 'முதன்மை நிர்வாகி',
  'admin.role.verification_officer': 'சரிபார்ப்பு அதிகாரி',
  'admin.role.support_csr': 'ஆதரவு அதிகாரி',
  'admin.role.finance_officer': 'நிதி அதிகாரி',
  'admin.role.auditor': 'தணிக்கையாளர்',
  'admin.role.driver': 'ஓட்டுநர்',
  'admin.role.passenger': 'பயணி',
  'admin.role.fleet_owner': 'வாகனத் தொகுதி உரிமையாளர்',

  /* ---- Nav groups ------------------------------------------------------ */
  'nav.group.overview': 'மேலோட்டம்',
  'nav.group.onboarding': 'பதிவு',
  'nav.group.directories': 'அடைவு',
  'nav.group.moderation': 'மட்டுறுத்தலும் ஆதரவும்',
  'nav.group.finance': 'நிதி',
  'nav.group.configuration': 'கட்டமைப்பு',
  'nav.group.access': 'அணுகல்',

  /* ---- Nav items ------------------------------------------------------- */
  'nav.dashboard': 'கட்டுப்பாட்டுப் பலகை',
  'nav.auditLog': 'தணிக்கைப் பதிவு',
  'nav.verification': 'சரிபார்ப்பு வரிசைகள்',
  'nav.documentExpiry': 'காலாவதியாகும் ஆவணங்கள்',
  'nav.passengers': 'பயணிகள்',
  'nav.drivers': 'ஓட்டுநர்கள்',
  'nav.vehicles': 'வாகனங்கள்',
  'nav.reports': 'வாகனப் புகார்கள்',
  'nav.supportTickets': 'ஆதரவுச் சீட்டுகள்',
  'nav.fraudReview': 'மோசடி மறுஆய்வு',
  'nav.reconciliation': 'கணக்கு ஒப்பீடு',
  'nav.transactions': 'பரிவர்த்தனைகள்',
  'nav.refunds': 'பணத் திருப்பிச் செலுத்தல்கள்',
  'nav.walletAdjustments': 'பணப்பைச் சரிசெய்தல்கள்',
  'nav.pdpa': 'தரவு உரிமைகள்',
  'nav.fareTariffs': 'கட்டண விகிதங்கள்',
  'nav.cities': 'நகரங்கள்',
  'nav.featureFlags': 'அம்சக் கொடிகள்',
  'nav.trains': 'ரயில்கள்',
  'nav.announcements': 'அறிவிப்புகள்',
  'nav.gtfs': 'போக்குவரத்துத் தரவு (GTFS)',
  'nav.dailyFeeRates': 'தினசரிக் கட்டண விகிதங்கள்',
  'nav.voucherTiers': 'வவுச்சர் அடுக்குகள்',
  'nav.driverLevels': 'ஓட்டுநர் நிலைகள்',
  'nav.rbac': 'பயனர்களும் பாத்திரங்களும்',
};

export default adminTa;
