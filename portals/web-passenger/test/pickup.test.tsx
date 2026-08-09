import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { PickupConfirm } from '@/components/PickupConfirm';

/**
 * **SCR-WT-003, and the one promise it has to keep** (US-25.3, AL-45, P-02).
 *
 * The screen tells an unregistered rider, before they have decided anything, that
 * "declining never shares your GPS". `test/fences.test.ts` proves the *shape* of
 * that — no parameter, no body, no field anywhere on the path that could carry a
 * coordinate. This proves the *behaviour*: pressing Decline calls the action that
 * takes no coordinate, and the browser's Geolocation API is never touched.
 *
 * That last one matters more than it sounds. A page that called
 * `getCurrentPosition` on mount would have taken a position before the reader had
 * read the sentence promising it would not — and would have done it on the phone of
 * somebody with no MageRide account, at the moment they opened an SMS.
 */

const share = vi.fn();
const decline = vi.fn();
const refresh = vi.fn();

vi.mock('@/server/track-actions', () => ({
  sharePickupLocation: (...args: unknown[]) => share(...args),
  declinePickupLocation: (...args: unknown[]) => decline(...args),
}));

vi.mock('next/navigation', () => ({ useRouter: () => ({ refresh, push: vi.fn() }) }));

vi.mock('@/components/LazyTrackMap', () => ({
  LazyTrackMap: ({ pin }: { pin?: { lat: number; lng: number } }) => (
    <div data-testid="map" data-pin={pin ? `${pin.lat},${pin.lng}` : ''} />
  ),
}));

const getCurrentPosition = vi.fn();

const TOKEN = 'Zx8CvBnMaSdFgHjKlQwErTyUiOp01234';

function pickup(props: Partial<React.ComponentProps<typeof PickupConfirm>> = {}) {
  return render(
    <PickupConfirm
      token={TOKEN}
      locale="en"
      styleUrl={null}
      bookerFirstName="Ramith"
      suggestedPin={{ lat: 6.9271, lng: 79.8612 }}
      ttlRemainingSec={278}
      {...props}
    />,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  share.mockResolvedValue({ ok: true });
  decline.mockResolvedValue({ ok: true });
  Object.defineProperty(globalThis.navigator, 'geolocation', {
    configurable: true,
    value: { getCurrentPosition },
  });
});

afterEach(cleanup);

describe('the banner', () => {
  it('states the countdown and the promise together, as the wireframe does', () => {
    pickup();
    expect(screen.getByText(/Expires in 4:38 · declining never shares your GPS/)).toBeTruthy();
  });

  it('counts down from public-bff’s own remaining seconds, not from a clock', () => {
    // `ttlRemainingSec` is `issued_at + ttl_seconds` evaluated server-side — the
    // durable fact ride-svc's sweep reads too. A phone whose clock is an hour out
    // still counts five minutes.
    pickup({ ttlRemainingSec: 65 });
    expect(screen.getByText(/Expires in 1:05/)).toBeTruthy();
  });
});

describe('declining', () => {
  it('sends no coordinate — the action is called with the token alone', async () => {
    pickup();
    fireEvent.click(screen.getByRole('button', { name: 'Decline' }));

    await waitFor(() => expect(decline).toHaveBeenCalledTimes(1));
    expect(decline.mock.calls[0]).toEqual([TOKEN]);
  });

  it('never asks the browser where the reader is', async () => {
    pickup();
    fireEvent.click(screen.getByRole('button', { name: 'Decline' }));

    await waitFor(() => expect(decline).toHaveBeenCalled());
    expect(getCurrentPosition).not.toHaveBeenCalled();
  });

  it('says afterwards that nothing was sent and nothing was stored', async () => {
    pickup();
    fireEvent.click(screen.getByRole('button', { name: 'Decline' }));

    await waitFor(() =>
      expect(
        screen.getByText(
          'You declined. No location was sent, and nothing about where you are was stored.',
        ),
      ).toBeTruthy(),
    );
  });
});

describe('sharing', () => {
  it('sends the suggested pin when the reader accepts it as it is', async () => {
    pickup();
    fireEvent.click(screen.getByRole('button', { name: 'Share location' }));

    await waitFor(() => expect(share).toHaveBeenCalledTimes(1));
    // No `accuracy`: nothing measured this point, so there is no metres figure that
    // would be true about it.
    expect(share.mock.calls[0]).toEqual([TOKEN, { lat: 6.9271, lng: 79.8612 }]);
  });

  it('asks the browser only when the reader presses the button that says so', async () => {
    getCurrentPosition.mockImplementation((ok: PositionCallback) =>
      ok({ coords: { latitude: 6.93, longitude: 79.85, accuracy: 12 } } as GeolocationPosition),
    );

    pickup();
    expect(getCurrentPosition).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Use my current location' }));
    await waitFor(() => expect(getCurrentPosition).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'Share location' }));
    await waitFor(() => expect(share).toHaveBeenCalled());

    // The metres the browser reported describe *this* fix, so they travel with it.
    expect(share.mock.calls[0]).toEqual([TOKEN, { lat: 6.93, lng: 79.85, accuracy: 12 }]);
  });

  it('tells the reader to place the pin themselves when the browser refuses', async () => {
    getCurrentPosition.mockImplementation((_ok: PositionCallback, fail: PositionErrorCallback) =>
      fail({ code: 1, message: 'denied' } as GeolocationPositionError),
    );

    pickup();
    fireEvent.click(screen.getByRole('button', { name: 'Use my current location' }));

    await waitFor(() =>
      expect(
        screen.getByText(
          'Your browser did not share a location. Drag the pin to where you are instead.',
        ),
      ).toBeTruthy(),
    );
    // A refusal is a decision, not a failure: nothing was sent either way.
    expect(share).not.toHaveBeenCalled();
  });

  it('cannot be pressed at all with no pin to send', () => {
    pickup({ suggestedPin: undefined });

    expect(screen.getByRole('button', { name: 'Share location' }).hasAttribute('disabled')).toBe(
      true,
    );
    expect(screen.getByText('Drag the pin to where you are, or use your current location.')).toBeTruthy();
  });

  it('reloads into the dead end when the link died mid-decision', async () => {
    // The five minutes elapsed, or the request was already answered. The *server*
    // re-reads the token and renders SCR-WT-006 — the page does not decide it.
    share.mockResolvedValue({ ok: false, dead: true });

    pickup();
    fireEvent.click(screen.getByRole('button', { name: 'Share location' }));

    await waitFor(() => expect(refresh).toHaveBeenCalledTimes(1));
  });
});

describe('expiry', () => {
  it('closes the screen when the countdown runs out', () => {
    pickup({ ttlRemainingSec: 0 });

    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain(
      'This request has expired',
    );
    expect(screen.getByText(/Ramith can set the pickup point on the map instead/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Share location' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Decline' })).toBeNull();
  });
});
