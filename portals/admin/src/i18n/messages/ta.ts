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

  /* ---- SCR-AP-003 · verification queues (AL-39, C106) ------------------ */
  'admin.verification.queue.navLabel': 'சரிபார்ப்பு வரிசைகள்',
  'admin.verification.queue.drivingLicence': 'சாரதி அனுமதிப்பத்திரம் நிலுவையில்',
  'admin.verification.queue.vehicleRegistration': 'வாகனப் பதிவு நிலுவையில்',
  'admin.verification.queue.fleetOrg': 'வாகனத் தொகுதி அனுமதி',
  'admin.verification.queue.headingDrivingLicence': 'சாரதி அனுமதிப்பத்திரச் சரிபார்ப்புகள் — நிலுவையில்',
  'admin.verification.queue.headingVehicleRegistration': 'வாகனப் பதிவுச் சரிபார்ப்புகள் — நிலுவையில்',
  'admin.verification.queue.headingFleetOrg': 'வாகனத் தொகுதி நிறுவனங்கள் — அனுமதிக்குக் காத்திருக்கிறது',
  'admin.verification.queue.caption': 'நிலுவையில் உள்ள சரிபார்ப்புகள்',
  'admin.verification.queue.flagsOnly': 'கையால் இட்ட / சந்தேகமான குறியீடுகள் மட்டும்',
  'admin.verification.queue.orgGate': 'எந்த வாகனத் தொகுதிச் செயலுக்கும் முன் அனுமதி வாயில்',
  'admin.verification.queue.search': 'தேடு',
  'admin.verification.queue.searchHint':
    'ஓட்டுநர், வாகனம் அல்லது நிறுவனம் — பெயர், பதிவு இலக்கம் அல்லது அடையாளத்தால்.',
  'admin.verification.queue.status': 'நிலை',
  'admin.verification.queue.statusAll': 'எந்த நிலையும்',
  'admin.verification.queue.apply': 'பயன்படுத்து',
  'admin.verification.queue.clear': 'அழி',
  'admin.verification.queue.review': 'மீளாய்வு',
  'admin.verification.queue.empty': 'இந்த வரிசையில் எதுவும் காத்திருக்கவில்லை.',
  'admin.verification.queue.total': '{count} நிலுவையில்',
  'admin.verification.queue.totalMore': '{count}+ நிலுவையில்',
  'admin.verification.queue.countMore': '{count}+',
  'admin.verification.queue.capped': 'முதல் {count} காட்டப்படுகிறது. மீதியை அடைய தேடலைக் குறுக்குங்கள்.',
  'admin.verification.status.pendingCount': 'நிலுவையில் · {count}',

  'admin.verification.column.driver': 'ஓட்டுநர்',
  'admin.verification.column.vehicle': 'வாகனம்',
  'admin.verification.column.organisation': 'நிறுவனம்',
  'admin.verification.column.submitted': 'சமர்ப்பித்தது',
  'admin.verification.column.flagged': 'குறியிடப்பட்ட புலங்கள்',
  'admin.verification.column.vehicles': 'வாகனங்கள்',
  'admin.verification.column.evidence': 'சான்று',
  'admin.verification.column.field': 'புலம்',
  'admin.verification.column.value': 'மதிப்பு',
  'admin.verification.column.source': 'மூலம்',
  'admin.verification.column.status': 'நிலை',
  'admin.verification.column.action': 'செயல்',

  'admin.verification.decided.approved': 'அனுமதிக்கப்பட்டது.',
  'admin.verification.decided.rejected': 'நிராகரிக்கப்பட்டது.',

  /* ---- SCR-AP-003a ----------------------------------------------------- */
  'admin.verification.detail.back': 'வரிசைக்குத் திரும்பு',
  'admin.verification.detail.pendingFields': 'மீளாய்வு நிலுவையில் · குறியிடப்பட்ட {count} புலங்கள்',
  'admin.verification.detail.pendingReview': 'மீளாய்வு நிலுவையில்',
  'admin.verification.detail.readyToApprove': 'எல்லாப் புலங்களும் உறுதிப்படுத்தப்பட்டன',

  'admin.verification.doc.heading': 'இணைக்கப்பட்ட ஆவணங்கள்',
  'admin.verification.doc.hint': 'முழு அளவில் திறக்க ஒரு சிறுபடத்தைத் தட்டுங்கள்',
  'admin.verification.doc.empty': 'இந்தச் சமர்ப்பிப்புடன் ஆவணங்கள் இணைக்கப்படவில்லை.',
  'admin.verification.doc.note':
    'ஒவ்வொரு சிறுபடமும் சேமிக்கப்பட்ட பதிவு ஆவணம். ஒன்றைத் திறப்பதும் தணிக்கைப் பதிவில் எழுதப்படும்.',
  'admin.verification.doc.position': '{index} / {total}',
  'admin.verification.doc.capturedDragCrop': 'மூல பதிவேற்றம் · செயலியின் ஸ்கேனரால் எடுக்கப்பட்டது',
  'admin.verification.doc.capturedUpload': 'மூல பதிவேற்றம் · கேலரியிலிருந்து தேர்ந்தெடுக்கப்பட்டது',
  'admin.verification.doc.drivingLicense': 'சாரதி அனுமதிப்பத்திரம்',
  'admin.verification.doc.registration': 'பதிவு',
  'admin.verification.doc.permit': 'வழி அனுமதிப்பத்திரம்',
  'admin.verification.doc.insurance': 'காப்புறுதி',
  'admin.verification.doc.revenueLicense': 'வருமான அனுமதிப்பத்திரம்',
  'admin.verification.doc.vehiclePhoto': 'வாகனப் புகைப்படம்',
  'admin.verification.doc.bankStatement': 'வங்கி அறிக்கை',
  'admin.verification.doc.passbookFirstPage': 'வங்கிப் புத்தகத்தின் முதல் பக்கம்',
  'admin.verification.doc.proofOfAccount': 'கணக்குச் சான்று',
  'admin.verification.doc.lankaQr': 'LankaQR குறியீடு',

  'admin.verification.fields.heading': 'AI எடுத்த புலங்கள்',
  'admin.verification.fields.engine': 'Gemini Flash 3.0 · தனிநபர் தரவு மறைக்கப்பட்டது',
  'admin.verification.fields.empty': 'இந்தச் சமர்ப்பிப்பிலிருந்து எதுவும் எடுக்கப்படவில்லை.',
  'admin.verification.fields.note':
    'ஓட்டுநரே தட்டச்சு செய்திருந்தால், ஸ்கேன் சந்தேகமாக இருந்தால், அல்லது இலக்கத்தகடு பதிவு இலக்கத்துடன் பொருந்தாவிட்டால் அந்த வரி நிலுவையில் இருக்கும். ஒவ்வொன்றையும் உறுதிப்படுத்த வேண்டும், அல்லது திருத்தி உறுதிப்படுத்த வேண்டும்.',

  'admin.verification.field.licenceNo': 'அனுமதிப்பத்திர இலக்கம்',
  'admin.verification.field.licenceExpiry': 'அனுமதிப்பத்திரம் முடிவடையும் திகதி',
  'admin.verification.field.nicNo': 'தே.அ.அ. இலக்கம்',
  'admin.verification.field.allowedVehicleTypes': 'அனுமதிக்கப்பட்ட வாகன வகைகள்',
  'admin.verification.field.insuranceExpiry': 'காப்புறுதி முடிவடையும் திகதி',
  'admin.verification.field.revenueNo': 'வருமான அனுமதிப்பத்திர இலக்கம்',
  'admin.verification.field.revenueExpiry': 'வருமான அனுமதிப்பத்திரம் முடிவடையும் திகதி',
  'admin.verification.field.regNoMatch': 'பதிவு இலக்கமும் இலக்கத்தகடும்',
  'admin.verification.field.editConfirm': 'திருத்தி உறுதிப்படுத்து',
  'admin.verification.field.confirmNamed': '{field} உறுதிப்படுத்து',
  'admin.verification.field.editNamed': '{field} திருத்து',
  'admin.verification.field.correctedValue': 'திருத்திய மதிப்பு',
  'admin.verification.field.working': 'பதிவு செய்கிறது…',
  'admin.verification.field.valueRequired':
    'திருத்திய மதிப்பைத் தட்டச்சு செய்யுங்கள், அல்லது எடுக்கப்பட்டதை ஏற்க உறுதிப்படுத்து அழுத்துங்கள்.',

  'admin.verification.source.ai': 'AI',
  'admin.verification.source.aiScored': 'AI {confidence}',
  'admin.verification.source.manual': 'கையால்',
  'admin.verification.fieldStatus.autoVerified': 'தானாகச் சரிபார்க்கப்பட்டது',
  'admin.verification.fieldStatus.confirmed': 'உறுதிப்படுத்தப்பட்டது',
  'admin.verification.fieldStatus.pendingDoubtful': 'நிலுவையில் · சந்தேகம்',
  'admin.verification.fieldStatus.pendingMismatch': 'நிலுவையில் · பொருந்தவில்லை',

  'admin.verification.step.profile': 'சுயவிவரம் / அனுமதிப்பத்திரம்',
  'admin.verification.step.details': 'வாகன விவரங்கள்',
  'admin.verification.step.insurance': 'காப்புறுதி',
  'admin.verification.step.revenue': 'வருமான அனுமதிப்பத்திரம்',
  'admin.verification.step.photos': 'வாகனப் புகைப்படங்கள்',
  'admin.verification.step.registration': 'பதிவு',
  'admin.verification.step.permit': 'வழி அனுமதிப்பத்திரம்',
  'admin.verification.step.kyc': 'நிறுவன KYC',
  'admin.verification.step.awaitingUpload': 'பதிவேற்றப்படவில்லை',

  'admin.verification.decision.heading': 'தீர்ப்பு',
  'admin.verification.decision.steps': 'பதிவுப் படிகள்',
  'admin.verification.decision.reason': 'நிராகரிப்புக் காரணம் (இருந்தால்)',
  'admin.verification.decision.reasonHint': 'எழுதியவாறே விண்ணப்பதாரருக்குக் காட்டப்படும்.',
  'admin.verification.decision.approveDriver': 'ஓட்டுநரை அனுமதி',
  'admin.verification.decision.approveVehicle': 'வாகனத்தை அனுமதி',
  'admin.verification.decision.approveOrg': 'நிறுவனத்தை அனுமதி',
  'admin.verification.decision.reject': 'காரணத்துடன் நிராகரி',
  'admin.verification.decision.working': 'பதிவு செய்கிறது…',
  'admin.verification.approve.blocked':
    'நிலுவையில் உள்ள ஒவ்வொரு புலமும் உறுதிப்படுத்தப்பட்ட பின் அனுமதி திறக்கும்.',
  'admin.verification.reject.reasonRequired':
    'ஒரு காரணம் தாருங்கள். எழுதியவாறே விண்ணப்பதாரருக்குக் காட்டப்படும்.',

  /* ---- SCR-AP-003b ----------------------------------------------------- */
  'admin.verification.viewer.title': '{document} · {position}',
  'admin.verification.viewer.previous': 'முந்தையது',
  'admin.verification.viewer.zoomIn': 'பெரிதாக்கு',
  'admin.verification.viewer.zoomOut': 'சிறிதாக்கு',
  'admin.verification.viewer.rotate': 'கால் சுற்று சுழற்று',
  'admin.verification.viewer.reset': 'பெரிதாக்கலையும் சுழற்சியையும் மீட்டமை',

  /* ---- SCR-AP-003c ----------------------------------------------------- */
  'admin.verification.org.vehicleCount': '{count} வாகனங்கள்',
  'admin.verification.org.kycComplete': 'KYC முழுமை',
  'admin.verification.org.kycIncomplete': 'KYC முழுமையற்றது',
  'admin.verification.org.heading': 'நிறுவன KYC',
  'admin.verification.org.caption': 'நிறுவன KYC விவரங்கள்',
  'admin.verification.org.registeredName': 'பதிவு செய்யப்பட்ட பெயர்',
  'admin.verification.org.registrationNo': 'வணிகப் பதிவு இலக்கம்',
  'admin.verification.org.contactPhone': 'அங்கீகரிக்கப்பட்ட தொடர்பு',
  'admin.verification.org.contactEmail': 'தொடர்பு மின்னஞ்சல்',
  'admin.verification.org.address': 'பதிவு செய்யப்பட்ட முகவரி',
  'admin.verification.org.rejectionReason': 'நிராகரிப்புக் காரணம்',
  'admin.verification.org.payoutHeading': 'வங்கி மற்றும் கொடுப்பனவு விவரங்கள்',
  'admin.verification.org.payoutCaption': 'வங்கி மற்றும் கொடுப்பனவு விவரங்கள்',
  'admin.verification.org.payoutNone': 'இந்த நிறுவனம் இன்னும் வங்கி விவரங்களைச் சமர்ப்பிக்கவில்லை.',
  'admin.verification.org.bank': 'வங்கி',
  'admin.verification.org.branch': 'கிளை',
  'admin.verification.org.accountNo': 'கணக்கு இலக்கம்',
  'admin.verification.org.accountHolder': 'கணக்கு வைத்திருப்பவரின் பெயர்',
  'admin.verification.org.payoutRejection': 'கொடுப்பனவு நிராகரிப்புக் காரணம்',
  'admin.verification.org.payoutGate':
    'நிறுவனத்தை அனுமதிப்பது இந்த விவரங்களைச் சரிபார்க்கிறது. அதுவரை கட்டணம் செலுத்தும் வாகனமோ கட்டணச் சந்தாவோ வசூலிக்க முடியாது.',
  'admin.verification.org.documents': 'இணைக்கப்பட்ட சான்று',
  'admin.verification.org.documentsEmpty': 'இந்த நிறுவனம் இன்னும் ஆவணங்களை இணைக்கவில்லை.',
  'admin.verification.payout.pending': 'கொடுப்பனவு நிலுவையில்',
  'admin.verification.payout.verified': 'கொடுப்பனவு சரிபார்க்கப்பட்டது',
  'admin.verification.payout.rejected': 'கொடுப்பனவு நிராகரிக்கப்பட்டது',
  'admin.verification.payout.superseded': 'கொடுப்பனவு மாற்றப்பட்டது',

  /* ---- SCR-AP-004 · moderation ----------------------------------------- */
  'admin.moderation.queue.heading': 'வாகனப் புகார்கள் — மீளாய்வு நிலுவையில்',
  'admin.moderation.queue.caption': 'தீர்மானம் நிலுவையிலுள்ள வாகனப் புகார்கள்',
  'admin.moderation.queue.rule': 'உறுதிப்படுத்தப்பட்ட {count} புகார்கள் வாகனத்தை நீக்கும்',
  'admin.moderation.queue.scope':
    'இன்னும் யாரும் தீர்மானிக்காத புகார்கள். உறுதிப்படுத்தப்பட்ட அல்லது நிராகரிக்கப்பட்ட புகார் இந்த வரிசையிலிருந்து விலகும்.',
  'admin.moderation.queue.total': '{count} நிலுவையில்',
  'admin.moderation.queue.totalMore': '{count}+ நிலுவையில்',
  'admin.moderation.queue.capped': 'முதல் {count} காட்டப்படுகிறது.',
  'admin.moderation.queue.empty': 'உங்களிடம் நிலுவையில் வாகனப் புகார் எதுவும் இல்லை.',

  'admin.moderation.column.subject': 'பொருள்',
  'admin.moderation.column.reports': 'புகார்கள்',
  'admin.moderation.column.reason': 'காரணம்',
  'admin.moderation.column.raised': 'பதிவு செய்யப்பட்டது',
  'admin.moderation.column.action': 'செயல்',

  'admin.moderation.report.pendingCount': '{count} நிலுவையில்',
  'admin.moderation.report.noReason': 'காரணம் தரப்படவில்லை',
  'admin.moderation.report.suspendVehicle': 'இந்த வாகனத்தை இடைநிறுத்து',
  'admin.moderation.report.confirm': 'புகாரை உறுதிப்படுத்து',
  'admin.moderation.report.dismiss': 'நிராகரி',
  'admin.moderation.report.working': 'பதிவு செய்கிறது…',
  'admin.moderation.report.confirmNamed': '{vehicle} வாகனத்துக்கு எதிரான புகாரை உறுதிப்படுத்து',
  'admin.moderation.report.dismissNamed': '{vehicle} வாகனத்துக்கு எதிரான புகாரை நிராகரி',

  'admin.moderation.verdict.confirmed': 'புகார் உறுதிப்படுத்தப்பட்டது.',
  'admin.moderation.verdict.confirmedCount':
    'புகார் உறுதிப்படுத்தப்பட்டது. இந்த வாகனத்துக்கு இப்போது உறுதிப்படுத்தப்பட்ட {count} புகார்கள் உள்ளன; மேலும் {remaining} அதை நீக்கும்.',
  'admin.moderation.verdict.delisted':
    'புகார் உறுதிப்படுத்தப்பட்டது. அது உறுதிப்படுத்தப்பட்ட {count} புகார்கள் என்பதால் வாகனம் நீக்கப்பட்டது.',
  'admin.moderation.verdict.dismissed': 'புகார் நிராகரிக்கப்பட்டது.',

  'admin.moderation.suspend.heading': 'இடைநிறுத்தம் / தடை',
  'admin.moderation.suspend.subject': 'இடைநிறுத்துவது',
  'admin.moderation.suspend.driver': 'ஓட்டுநர்',
  'admin.moderation.suspend.vehicle': 'வாகனம்',
  'admin.moderation.suspend.subjectId': 'ஓட்டுநர் / வாகன ID',
  'admin.moderation.suspend.subjectIdHint': 'பதிவேட்டில் உள்ளவாறே தளத்தின் அடையாள இலக்கம்.',
  'admin.moderation.suspend.reason': 'காரணம்',
  'admin.moderation.suspend.reasonHint': 'கட்டாயம்; உங்கள் பெயருடன் பதிவு செய்யப்படும்.',
  'admin.moderation.suspend.apply': 'பயன்படுத்து',
  'admin.moderation.suspend.working': 'பதிவு செய்கிறது…',
  'admin.moderation.suspend.idRequired': 'பதிவேட்டில் உள்ளவாறே அடையாள இலக்கத்தை உள்ளிடுங்கள்.',
  'admin.moderation.suspend.reasonRequired':
    'ஒரு காரணத்தைத் தாருங்கள். அது தணிக்கைப் பதிவில் எழுதப்படும், மேல்முறையீட்டுக்கு பதில் அதிலிருந்தே வரும்.',
  'admin.moderation.suspend.noDuration':
    'யாராவது நீக்கும் வரை இடைநிறுத்தம் நீடிக்கும். தேர்ந்தெடுக்க கால அளவு இல்லை, தானாக மீட்கப்படுவதும் இல்லை.',
  'admin.moderation.suspend.doneDriver':
    'ஓட்டுநர் இடைநிறுத்தப்பட்டார். அவரது அமர்வு முடிந்தது, புதிய பயணங்கள் வழங்கப்படாது; ஏற்கெனவே நடக்கும் பயணம் முடிய அனுமதிக்கப்படும்.',
  'admin.moderation.suspend.doneVehicle':
    'வாகனம் இடைநிறுத்தப்பட்டது. அது பயண ஒதுக்கீட்டிலிருந்தும் நேரடி வரைபடத்திலிருந்தும் விலகியது.',

  /* ---- SCR-AP-005 · support & disputes ---------------------------------- */
  'admin.support.filter.status': 'நிலை',
  'admin.support.filter.statusAll': 'எந்த நிலையும்',
  'admin.support.filter.category': 'வகை',
  'admin.support.filter.categoryHint': 'சேமிக்கப்பட்ட வகைச் சாவி, எ.கா. driver_qr_dispute.',
  'admin.support.filter.apply': 'பயன்படுத்து',
  'admin.support.filter.clear': 'அழி',

  'admin.support.status.open': 'திறந்துள்ளது',
  'admin.support.status.inProgress': 'கையாளப்படுகிறது',
  'admin.support.status.resolved': 'தீர்க்கப்பட்டது',

  'admin.support.category.dailyFeeRefund': 'தினசரிக் கட்டணத் திருப்பிச் செலுத்தல் கோரிக்கை',
  'admin.support.category.driverQrDispute': 'ஓட்டுநர் QR கொடுப்பனவுத் தகராறு',

  'admin.support.queue.heading': 'வரிசை',
  'admin.support.queue.empty': 'இந்த வடிகட்டலுக்குப் பொருந்தும் சீட்டு இல்லை.',
  'admin.support.queue.finance': 'நிதி',
  'admin.support.queue.total': 'இந்த வரிசையில் {count}',
  'admin.support.queue.totalMore': 'இந்த வரிசையில் {count}+',
  'admin.support.queue.capped': 'முதல் {count} காட்டப்படுகிறது. மீதியை அடைய வடிகட்டலைக் குறுக்குங்கள்.',

  'admin.support.detail.raisedBy': 'பதிவு செய்தவர்',
  'admin.support.detail.noneHeading': 'எந்தச் சீட்டும் திறக்கப்படவில்லை',
  'admin.support.detail.noneBody': 'படிக்க வரிசையிலிருந்து ஒரு சீட்டைத் தேர்ந்தெடுங்கள்.',
  'admin.support.detail.notInView':
    'நீங்கள் வடிகட்டியுள்ள பகுதியில் அந்தச் சீட்டு இல்லை. அதைக் கண்டறிய வடிகட்டலை அழியுங்கள்.',

  'admin.support.thread.heading': 'உரையாடல்',
  'admin.support.thread.empty': 'இந்தச் சீட்டில் செய்தி எதுவும் இல்லை.',
  'admin.support.thread.raiser': 'பதிவு செய்த நபர்',
  'admin.support.thread.agent': 'MageRide ஆதரவு',

  'admin.support.lookup.heading': 'படிக்க மட்டுமான தேடல்',
  'admin.support.lookup.passenger': 'பயணியின் பதிவேட்டைத் திற',
  'admin.support.lookup.driver': 'ஓட்டுநரின் பதிவேட்டைத் திற',
  'admin.support.lookup.note':
    'அடைவுப் பதிவேடு படிக்க மட்டுமே, ஒன்றைத் திறப்பதும் தணிக்கைப் பதிவில் எழுதப்படும்.',
  'admin.support.lookup.none': 'அடைவுகள் உங்கள் பாத்திரத்தில் இல்லை.',

  'admin.support.refund.heading': 'திருப்பிச் செலுத்தல் கோரிக்கை',
  'admin.support.refund.note':
    'ஆதரவுப் பிரிவு பணத்தை நகர்த்தாது. திருப்பிச் செலுத்தலை நிதிப் பிரிவே திருப்பிச் செலுத்தல் வரிசையில் கோரி நிறைவேற்றும் — தினசரிக் கட்டணத் திருப்பமோ ஓட்டுநர் QR தகராறோ அதன் வகையாலேயே ஏற்கெனவே அந்த வரிசையில் உள்ளது.',
  'admin.support.refund.link': 'திருப்பிச் செலுத்தல் வரிசையைத் திற',

  'admin.support.resolved.heading': 'தீர்க்கப்பட்டது',
  'admin.support.resolved.note':
    'இந்தச் சீட்டு மூடப்பட்டது. பதிவு செய்த நபர் மேலுள்ள பதிலைத் தமது செயலியில் படிக்கலாம்.',

  'admin.support.resolve.response': 'உங்கள் பதில்',
  'admin.support.resolve.responseHint':
    'நீங்கள் எழுதியவாறே சீட்டைப் பதிவு செய்த நபருக்குக் காட்டப்படும்.',
  'admin.support.resolve.submit': 'தீர்',
  'admin.support.resolve.working': 'பதிவு செய்கிறது…',
  'admin.support.resolve.responseRequired':
    'முதலில் பதிலை எழுதுங்கள் — சீட்டைப் பதிவு செய்த நபருக்குக் காட்டப்படுவது அதுவே.',
  'admin.support.resolve.done': 'சீட்டு தீர்க்கப்பட்டது.',

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
