/**
 * English resources for the passenger web subview — and, because it is the only
 * locale declared with a literal object, the file that *defines* the key set.
 * `si.ts` and `ta.ts` are annotated `WebMessages`, so a key added here and not
 * there is a compile error, and a key there and not here is a compile error too.
 *
 * The trilingual rule is sharper on this surface than on either operator console.
 * A fleet manager chose to open a console; **this page is opened from an SMS by
 * somebody who has no MageRide account and did not ask to be here** — a package
 * recipient, or a rider somebody else booked a car for. There is no profile to
 * read a language preference off and no sign-in at which one could be chosen, so
 * `Accept-Language` and the `?lang=` switch in the top bar are the only two things
 * standing between the reader and a page in a language they do not speak.
 *
 * Keys are dotted and grouped by screen. Placeholders are `{name}`.
 */

export const webEn = {
  // The brand, and the six top-bar titles the wireframe gives the six screens.
  'web.appName': 'MageRide',
  // The square mark on the top bar. A resource rather than a literal because the
  // mark is a letterform, and a script that does not read Latin may want its own.
  'web.appMark': 'M',
  'web.bar.package': 'Package tracking',
  'web.bar.pickup': 'Share pickup',
  'web.bar.ride': 'Your ride',
  'web.bar.delivered': 'Delivered',
  'web.language.label': 'Language',

  // The ≤1 s gate. Shown while the token is being validated and never with data
  // beside it — D2 §SCR-WT-001, "no data rendered before validation".
  'web.loading.title': 'Checking your link…',

  // SCR-WT-001 · landing / token gate
  'web.landing.title': 'A package is on its way to you',
  'web.landing.body': '{driver} is delivering a package to you. Track it live — no app or login needed.',
  'web.landing.from': 'From',
  'web.landing.status': 'Status',
  'web.landing.driver': 'Driver',
  'web.landing.unknownSender': 'A MageRide sender',
  'web.landing.track': 'Track delivery live',
  'web.landing.expiry': 'This link expires when the package is delivered.',

  // SCR-WT-002 · package track (recipient)
  'web.package.progress': 'Delivery progress',
  'web.package.step.pending': 'Pending',
  'web.package.step.picked': 'Picked',
  'web.package.step.transit': 'In transit',
  'web.package.step.delivered': 'Delivered',
  'web.package.stepOf': 'Step {step} of {total}',
  'web.package.otpTitle': 'Show this Delivery OTP to the driver',
  'web.package.otpPending': 'Your delivery code appears here once the driver has the package.',
  'web.package.otpValue': 'Delivery code {code}',
  'web.package.senderLine': 'Sent by {sender}',

  // SCR-WT-003 · confirm pickup (unregistered proxy rider)
  'web.pickup.expiresIn': 'Expires in {clock}',
  // P-02. This half of the banner is the promise the screen has to keep, and it is
  // written before the reader has decided anything.
  'web.pickup.noGps': 'declining never shares your GPS',
  'web.pickup.title': '{name} is booking a ride for you',
  'web.pickup.body': 'Share your current pickup location so the driver can find you. No app or login needed.',
  'web.pickup.mapLabel': 'Your pickup location',
  'web.pickup.dragHint': 'Drag the pin to adjust',
  'web.pickup.pinLabel': 'Pickup pin',
  'web.pickup.useMyLocation': 'Use my current location',
  'web.pickup.locating': 'Finding your location…',
  'web.pickup.locationDenied': 'Your browser did not share a location. Drag the pin to where you are instead.',
  'web.pickup.noPin': 'Drag the pin to where you are, or use your current location.',
  'web.pickup.share': 'Share location',
  'web.pickup.sharing': 'Sharing…',
  'web.pickup.decline': 'Decline',
  'web.pickup.declining': 'Declining…',
  'web.pickup.shared': 'Your pickup location has been shared with {name}.',
  'web.pickup.declined': 'You declined. No location was sent, and nothing about where you are was stored.',
  'web.pickup.expiredTitle': 'This request has expired',
  'web.pickup.expiredBody': 'The five minutes are up, so nothing was sent. {name} can set the pickup point on the map instead.',
  'web.pickup.closed': 'This link is now closed.',

  // SCR-WT-004 · ride track (proxy rider)
  'web.ride.thirdParty': 'Booked for you by someone else',
  'web.ride.state.matching': 'Finding a driver',
  'web.ride.state.arriving': 'Driver on the way',
  'web.ride.state.arrivingIn': 'Driver arriving · {minutes} min',
  'web.ride.state.waiting': 'Your driver is at the pickup',
  'web.ride.state.onTrip': 'On the way',
  'web.ride.state.onTripIn': 'On the way · {minutes} min',
  'web.ride.state.ended': 'This ride has ended',
  'web.ride.mapLabel': 'Your driver and the route',
  'web.ride.startOtpTitle': 'Tell the driver this Start OTP',
  'web.ride.noStartOtp': 'No start code is needed for this ride.',
  'web.ride.cashDue': 'Cash ride — pay the driver Rs {amount} at the end.',
  'web.ride.paidByBooker': 'Already paid by whoever booked this ride — Rs {amount}.',
  'web.ride.sos': 'SOS',
  'web.ride.sosOpen': 'Raise an emergency alert',
  'web.ride.sosTitle': 'Send an emergency alert?',
  'web.ride.sosBody': 'Your location is sent by SMS to the person who booked this ride, and MageRide safety is alerted at the same time.',
  'web.ride.sosSend': 'Send alert',
  'web.ride.sosSending': 'Sending…',
  'web.ride.sosSent': 'Alert sent. The person who booked this ride has been messaged.',
  'web.ride.sosNoContact': 'Alert recorded. MageRide safety can see it, but there was nobody to message.',
  'web.ride.sosFailed': 'Alert recorded, but the message could not be sent. Call for help directly as well.',
  'web.ride.sosUsedDriverPosition': 'Your browser did not share a location, so the driver’s last reported position was sent instead.',
  'web.ride.sosNoPosition': 'No location is available, so an alert cannot say where you are. Call for help directly.',

  // SCR-WT-005 · delivered / receipt
  'web.receipt.deliveredTitle': 'Package delivered',
  'web.receipt.rideTitle': 'Your trip is finished',
  'web.receipt.disputedTitle': 'This delivery is disputed',
  'web.receipt.otpVerified': 'Handed over at {time} · verified with the delivery code.',
  'web.receipt.photoProof': 'Handed over at {time} · nobody was there, so the driver photographed the drop-off.',
  'web.receipt.codCollected': 'Handed over at {time} · cash on delivery collected.',
  'web.receipt.disputedBody': 'Raised at {time}. MageRide support is looking into what happened.',
  'web.receipt.photoAlt': 'Photograph the driver took at the drop-off',
  'web.receipt.from': 'From',
  'web.receipt.driver': 'Driver',
  'web.receipt.completed': 'Completed',
  'web.receipt.payment': 'Payment',
  'web.receipt.paymentCod': 'Cash on delivery · Rs {amount} collected',
  'web.receipt.paymentAmount': 'Rs {amount}',
  'web.receipt.download': 'Download receipt',
  'web.receipt.settling': 'The handover is done. The receipt appears here once the payment has settled.',
  'web.receipt.closed': 'This link is now closed.',

  // SCR-WT-006 · expired / invalid link
  'web.expired.title': 'This link has expired',
  'web.expired.body': 'For your safety, tracking and pickup links are time-limited and single-use. Ask the sender to share a new link, or open the MageRide app.',
  'web.expired.open': 'Open MageRide',

  // The app strip the wireframe puts under SCR-WT-002 and SCR-WT-005.
  'web.app.deliveriesPrompt': 'Want your own deliveries?',
  'web.app.sendPrompt': 'Send packages yourself',
  'web.app.get': 'Get the app',

  // The driver card, shared by SCR-WT-002 and SCR-WT-004. AL-48: the number is
  // the driver's real one and the control is a plain `tel:` link.
  'web.driver.call': 'Call',
  'web.driver.callDriver': 'Call driver',
  'web.driver.callAria': 'Call {name} on {phone}',
  'web.driver.noPhone': 'This driver’s number is not available on this link.',
  'web.driver.photoAlt': 'Photograph of {name}',
  'web.driver.vehicleLine': '{type} · {reg}',

  // The live feed's own state. Visible because a page that quietly stopped
  // updating looks exactly like a vehicle that stopped moving.
  'web.live.on': 'Live',
  'web.live.reconnecting': 'Reconnecting…',
  'web.live.stopped': 'Live updates have stopped.',
  'web.live.lastFix': 'Position updated {since}',
  'web.live.noPosition': 'No position has been reported yet. The map fills in as soon as the vehicle reports one.',

  // The map.
  'web.map.noBasemap': 'The map background is unavailable here, so only the markers are drawn.',
  'web.map.zoomIn': 'Zoom in',
  'web.map.zoomOut': 'Zoom out',
  'web.map.attribution': 'Map credits',
  'web.map.metres': 'm',
  'web.map.kilometres': 'km',

  // The four `PackageStatus` values, as a reader reads them.
  'web.status.PickupPending': 'Waiting for pickup',
  'web.status.PickedUp': 'Picked up',
  'web.status.InTransit': 'On the way',
  'web.status.Delivered': 'Delivered',

  // `_shared.yaml#VehicleType` — the AL-09 canonical values, named.
  'web.vehicle.bike': 'Bike',
  'web.vehicle.three_wheeler': 'Three-wheeler',
  'web.vehicle.tuk': 'Tuk',
  'web.vehicle.sedan': 'Sedan',
  'web.vehicle.suv': 'SUV',
  'web.vehicle.van': 'Van',
  'web.vehicle.mini_van': 'Mini van',
  'web.vehicle.flex': 'Flex',
  'web.vehicle.bus': 'Bus',
  'web.vehicle.truck': 'Truck',
  'web.vehicle.mini_truck': 'Mini truck',
  'web.vehicle.unknown': 'Vehicle',

  // Failures. `problem.title` is never rendered — `_shared.yaml` says it is a
  // developer's English summary and is never localised — so every one of these is
  // reached from the problem's kebab code.
  'web.error.title': 'Something went wrong',
  'web.error.unexpected': 'Something went wrong. Please try again.',
  'web.error.badLocation': 'That location could not be read. Move the pin and try again.',
  'web.error.forbidden': 'This link cannot do that.',
  'web.error.rateLimited': 'This link has been opened too many times just now. Wait a moment and try again.',
  'web.error.receiptNotReady': 'The receipt is not ready yet. Check back once the payment has settled.',
  'web.error.serviceUnavailable': 'MageRide cannot be reached right now. Try again in a moment.',
  'web.error.reference': 'Reference: {traceId}',
  'web.error.retry': 'Try again',

  'web.notFound.title': 'Nothing here',
  'web.notFound.body': 'This address is not a MageRide tracking link. Open the link from your SMS again.',
} as const;

/** Every key this surface can render. Adding one here obliges `si.ts` and `ta.ts`. */
export type WebMessageKey = keyof typeof webEn;

/** A complete set of resources for one locale. */
export type WebMessages = Record<WebMessageKey, string>;
