import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { DeadEnd } from '@/components/DeadEnd';
import { DeliveredReceipt } from '@/components/DeliveredReceipt';
import { Landing } from '@/components/Landing';
import { PackageTrack } from '@/components/PackageTrack';
import { RideTrack } from '@/components/RideTrack';
import { createWebTranslator } from '@/i18n';

import {
  DRIVER,
  PACKAGE_TOKEN,
  RIDE_TOKEN,
  packageSnapshot,
  receipt,
  rideSnapshot,
} from './support/snapshots';

/**
 * The five server-rendered screens, against the wireframe's controls and states.
 *
 * The map is mocked out. It is a WebGL canvas that jsdom has no implementation for,
 * and everything these tests are about — the stepper, the code, the `tel:` link,
 * the fare notice, the outcome — is markup the server produced before any of it
 * loaded. That is the point being asserted as much as the markup is: a package
 * recipient's delivery code is in the first byte of HTML, not behind a bundle.
 */

vi.mock('@/components/LazyTrackMap', () => ({
  LazyTrackMap: () => <div data-testid="map" />,
}));

vi.mock('next/navigation', () => ({
  useRouter: () => ({ refresh: vi.fn(), push: vi.fn() }),
}));

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const t = createWebTranslator('en');
const en = { t, locale: 'en' as const, here: '/t/x' };

afterEach(cleanup);

describe('SCR-WT-001 · landing / token gate', () => {
  it('names the driver and offers the tracking link', () => {
    render(<Landing {...en} token={PACKAGE_TOKEN} snapshot={packageSnapshot()} />);

    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain('A package is on its way to you');
    expect(screen.getByRole('link', { name: 'Track delivery live' }).getAttribute('href')).toBe(`/t/${PACKAGE_TOKEN}`);
    expect(screen.getByText('Ramith')).toBeTruthy();
  });

  it('names the sender generically when the snapshot carries none', () => {
    // P-09 lets public-bff omit the display name; the card must not print
    // "undefined" at a recipient.
    render(
      <Landing
        {...en}
        token={PACKAGE_TOKEN}
        snapshot={packageSnapshot({ senderNameMasked: undefined })}
      />,
    );

    expect(screen.getByText('A MageRide sender')).toBeTruthy();
  });
});

describe('SCR-WT-002 · package track', () => {
  const track = (snapshot = packageSnapshot()) =>
    render(
      <PackageTrack
        {...en}
        token={PACKAGE_TOKEN}
        snapshot={snapshot}
        styleUrl={null}
        appUrl="https://play.google.com/store/apps/details?id=lk.mageride.passenger"
      />,
    );

  it('draws the four-step tracker and says which step it is on', () => {
    track();

    const progress = screen.getByRole('region', { name: 'Delivery progress' });
    expect(within(progress).getByText(/Step 3 of 4/)).toBeTruthy();
    expect(within(progress).getByText('In transit')).toBeTruthy();
  });

  it('shows the four-digit delivery code, server-rendered', () => {
    track();

    // One accessible string, four boxes. A screen reader reads out a code, not four
    // numbers, because the reader has to say it to a driver in one breath.
    expect(screen.getByLabelText('Delivery code 7 3 1 5')).toBeTruthy();
    expect(screen.getByText('Show this Delivery OTP to the driver')).toBeTruthy();
  });

  it('explains the missing code rather than drawing four empty boxes', () => {
    // public-bff emits `deliveryOtp` only while the parcel is aboard: before that
    // the driver has not been issued one, after it it is a live credential on a
    // finished job.
    track(packageSnapshot({ status: 'PickupPending', deliveryOtp: undefined }));

    expect(
      screen.getByText('Your delivery code appears here once the driver has the package.'),
    ).toBeTruthy();
    expect(screen.queryByText('7')).toBeNull();
  });

  it('dials the driver with a plain tel: link and no round trip (AL-48)', () => {
    track();

    const call = screen.getByRole('link', { name: `Call ${DRIVER.name} on ${DRIVER.phone}` });
    expect(call.getAttribute('href')).toBe(`tel:${DRIVER.phone}`);
  });

  it('says so when the link carries no number, rather than dialling nothing', () => {
    track(packageSnapshot({ driver: { ...DRIVER, phone: undefined } }));

    expect(screen.getByText('This driver’s number is not available on this link.')).toBeTruthy();
    expect(screen.queryByRole('link', { name: /Call/ })).toBeNull();
  });

  it('offers the app strip the wireframe draws under it', () => {
    track();
    expect(screen.getByRole('link', { name: 'Get the app' })).toBeTruthy();
  });

  it('draws no app strip at all when no store is configured', () => {
    render(
      <PackageTrack
        {...en}
        token={PACKAGE_TOKEN}
        snapshot={packageSnapshot()}
        styleUrl={null}
        appUrl={null}
      />,
    );

    expect(screen.queryByRole('link', { name: 'Get the app' })).toBeNull();
    expect(screen.queryByText('Want your own deliveries?')).toBeNull();
  });
});

describe('SCR-WT-004 · ride track', () => {
  const track = (snapshot = rideSnapshot()) =>
    render(<RideTrack {...en} token={RIDE_TOKEN} snapshot={snapshot} styleUrl={null} />);

  it('reads the ride state and the ETA as one chip', () => {
    track();
    expect(screen.getByText('Driver arriving · 3 min')).toBeTruthy();
  });

  it('drops the minutes when public-bff omitted the estimate', () => {
    // It omits `etaMin` when there is no fresh position, when the journey is over,
    // and when the straight-line estimate exceeds its own ceiling. A chip that said
    // "· undefined min" would be worse than one that says less.
    track(rideSnapshot({ etaMin: undefined }));
    expect(screen.getByText('Driver on the way')).toBeTruthy();
  });

  it('carries the third-party booking context D2 asks for', () => {
    track();
    expect(screen.getByText('Booked for you by someone else')).toBeTruthy();
  });

  it('tells a cash rider what they owe (US-8.21)', () => {
    track();
    expect(screen.getByText('Cash ride — pay the driver Rs 480 at the end.')).toBeTruthy();
  });

  it('says the booker paid, and never how (P-09)', () => {
    track(rideSnapshot({ fare: { totalMinor: 48_000, currency: 'LKR', paidBy: 'booker' } }));

    expect(screen.getByText(/Already paid by whoever booked this ride/)).toBeTruthy();
    // `PublicFareResponse` has no field for an instrument, so there is nothing to
    // print — this asserts the screen invents none.
    expect(screen.queryByText(/card|wallet|LankaQR/i)).toBeNull();
  });

  it('prints no fare block at all on a ride with no quote', () => {
    // "Rs 0.00" on a tracking page reads as "this is free".
    track(rideSnapshot({ fare: undefined }));
    expect(screen.queryByText(/Rs/)).toBeNull();
  });

  it('explains that no start code exists rather than showing empty boxes', () => {
    // `ride.yaml`: a rider start OTP is "accepted and ignored in this build: no
    // endpoint issues one", so public-bff omits `startOtp` on every proxy ride.
    track();
    expect(screen.getByText('No start code is needed for this ride.')).toBeTruthy();
    expect(screen.queryByText('Tell the driver this Start OTP')).toBeNull();
  });

  it('draws the Start OTP card if the platform ever issues one', () => {
    track(rideSnapshot({ startOtp: '4829' }));
    expect(screen.getByLabelText('Delivery code 4 8 2 9')).toBeTruthy();
  });

  it('puts Call driver and SOS in one row, as the wireframe does', () => {
    track();
    expect(screen.getByRole('link', { name: `Call ${DRIVER.name} on ${DRIVER.phone}` })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Raise an emergency alert' })).toBeTruthy();
  });
});

describe('SCR-WT-005 · delivered / receipt', () => {
  const delivered = (over = {}) =>
    render(
      <DeliveredReceipt
        {...en}
        kind="package"
        driver={DRIVER}
        senderName="Ramith"
        receipt={receipt(over)}
        appUrl={null}
      />,
    );

  it.each([
    ['otp_verified', 'verified with the delivery code'],
    ['photo_proof', 'the driver photographed the drop-off'],
    ['cod_collected', 'cash on delivery collected'],
  ] as const)('renders the %s outcome', (proof, sentence) => {
    delivered({ proof });
    expect(screen.getByText(new RegExp(sentence))).toBeTruthy();
  });

  it('renders a dispute as a dispute, not as a successful handover (P-14)', () => {
    delivered({ proof: 'disputed', state: 'Disputed' });

    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain('This delivery is disputed');
    expect(screen.queryByText('Package delivered')).toBeNull();
  });

  it('shows the doorstep photograph when there is one (P-10)', () => {
    delivered({ proof: 'photo_proof', proofPhotoUrl: 'https://objects.example/proof.jpg?sig=x' });

    expect(screen.getByAltText('Photograph the driver took at the drop-off').getAttribute('src')).toBe('https://objects.example/proof.jpg?sig=x');
  });

  it('offers the receipt download only once there is a receipt', () => {
    delivered();
    expect(screen.getByRole('button', { name: /Download receipt/ })).toBeTruthy();
  });

  it('renders the delivery without figures while the payment is still settling', () => {
    // `Completed` and `PaymentPending` are delivered and **not** receiptable, so
    // `GET …/receipt` answers 409. That is a state, not an error: telling a
    // recipient holding their parcel that something went wrong would be wrong.
    render(
      <DeliveredReceipt {...en} kind="package" driver={DRIVER} senderName="Ramith" receipt={null} appUrl={null} />,
    );

    expect(screen.getByText(/The handover is done/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Download receipt/ })).toBeNull();
  });

  it('heads a finished ride as a trip rather than as a delivery', () => {
    // The same screen and the same endpoint: US-25.6's four proof values are a
    // delivery's vocabulary and public-bff applies them to both kinds. What differs
    // is the heading and the strip — "Send packages yourself" under a finished taxi
    // ride would advertise the wrong product.
    render(
      <DeliveredReceipt
        {...en}
        kind="ride"
        driver={DRIVER}
        receipt={receipt({ kind: 'ride', state: 'CashSettled', proof: 'otp_verified' })}
        appUrl="https://play.example/mageride"
      />,
    );

    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain('Your trip is finished');
    expect(screen.queryByText('Send packages yourself')).toBeNull();
  });

  it('offers no way to call the driver once the parcel has arrived', () => {
    // public-bff omits `driver.phone` from a receipt: AL-48's `tel:` link exists so
    // a recipient can reach a driver who is on the way to them, and a receipt is a
    // document that gets forwarded.
    delivered();
    expect(screen.queryByRole('link', { name: /Call/ })).toBeNull();
  });
});

describe('SCR-WT-006 · expired / invalid link', () => {
  it('says the link has expired and offers the app', () => {
    render(<DeadEnd {...en} appUrl="https://apps.apple.com/lk/app/mageride/id1" />);

    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain('This link has expired');
    expect(screen.getByRole('link', { name: 'Open MageRide' }).getAttribute('href')).toBe('https://apps.apple.com/lk/app/mageride/id1');
  });

  it('holds no ride data anywhere in the document', () => {
    // The component takes none — `test/fences.test.ts` asserts its whole prop list
    // — and this is the same statement read off the rendered DOM: the driver, the
    // plate, the number, the sender and the code are all absent, which is C117's
    // "no ride data in the DOM" as an assertion rather than as a claim.
    const { container } = render(<DeadEnd {...en} appUrl={null} />);
    const html = container.innerHTML;

    for (const secret of [DRIVER.name, DRIVER.regNo, DRIVER.phone, 'Ramith', '7315', PACKAGE_TOKEN]) {
      expect(html, `SCR-WT-006 leaked "${secret}"`).not.toContain(secret);
    }
  });

  it('draws no store button when neither store is configured', () => {
    render(<DeadEnd {...en} appUrl={null} />);

    expect(screen.queryByRole('link', { name: 'Open MageRide' })).toBeNull();
    // Still a complete page: the copy already tells the reader what to do.
    expect(screen.getByText(/Ask the sender to share a new link/)).toBeTruthy();
  });
});

describe('the language switch', () => {
  it('offers all three, keeps the current path and drops a stale ?lang=', () => {
    render(<DeadEnd t={t} locale="en" here="/t/abc?foo=1" appUrl={null} />);

    expect(screen.getByRole('link', { name: 'සිංහල' }).getAttribute('href')).toBe('/t/abc?foo=1&lang=si');
    expect(screen.getByRole('link', { name: 'தமிழ்' }).getAttribute('href')).toBe('/t/abc?foo=1&lang=ta');
    expect(screen.getByRole('link', { name: 'English' }).getAttribute('href')).toBe('/t/abc?foo=1&lang=en');
  });

  it('renders the whole screen in the chosen language', () => {
    render(<DeadEnd t={createWebTranslator('ta')} locale="ta" here="/" appUrl={null} />);

    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain('இந்த இணைப்பு காலாவதியாகிவிட்டது');
  });
});
