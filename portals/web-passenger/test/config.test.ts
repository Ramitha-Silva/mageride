import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { appDownloadUrl, apiBaseUrl, mapStyleUrl, MissingConfigurationError } from '@/config/env';
import { formatAmountMinor, formatClock, formatCountdown, formatInstant } from '@/i18n/format';
import { hereWithout } from '@/server/render';

/**
 * Configuration and formatting — the two places a mistake is silent.
 *
 * A wrong gateway address fails loudly on the first request. A **missing** one, an
 * unset store link and a time zone read off the container's own clock all fail
 * quietly, in ways that look like the platform working: a 500 instead of a 503, a
 * button that does nothing, and a delivery stamped five and a half hours before it
 * happened.
 */

const ENVIRONMENT = { ...process.env };

beforeEach(() => {
  delete process.env['MAGERIDE_API_BASE_URL'];
  delete process.env['WEB_PASSENGER_MAP_STYLE_URL'];
  delete process.env['WEB_PASSENGER_ANDROID_APP_URL'];
  delete process.env['WEB_PASSENGER_IOS_APP_URL'];
});

afterEach(() => {
  process.env = { ...ENVIRONMENT };
});

describe('the gateway origin', () => {
  it('is required, and its absence is this process being unable to serve', () => {
    // Surfaced as a 503 by `apiFetch`, never as a 500: a deployment with no gateway
    // address is not a bad request from the visitor.
    expect(() => apiBaseUrl()).toThrowError(MissingConfigurationError);
  });

  it('drops a trailing slash so a path is never doubled', () => {
    process.env['MAGERIDE_API_BASE_URL'] = 'http://api-gateway:8080/';
    expect(apiBaseUrl()).toBe('http://api-gateway:8080');
  });
});

describe('the basemap', () => {
  it('is optional, and unset is a supported state', () => {
    // The driver marker renders on an empty canvas and the screen says so — a
    // missing basemap must not read as a missing driver.
    expect(mapStyleUrl()).toBeNull();
  });

  it('is read at request time, not baked into the image', () => {
    process.env['WEB_PASSENGER_MAP_STYLE_URL'] = 'https://tiles.example/style.json';
    expect(mapStyleUrl()).toBe('https://tiles.example/style.json');
  });
});

describe('the app-download link', () => {
  it('sends an iPhone to the App Store and everything else to Play', () => {
    process.env['WEB_PASSENGER_ANDROID_APP_URL'] = 'https://play.example/android';
    process.env['WEB_PASSENGER_IOS_APP_URL'] = 'https://apps.example/ios';

    expect(appDownloadUrl('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)')).toBe(
      'https://apps.example/ios',
    );
    expect(appDownloadUrl('Mozilla/5.0 (Linux; Android 15)')).toBe('https://play.example/android');
  });

  it('falls back to whichever store is configured', () => {
    process.env['WEB_PASSENGER_ANDROID_APP_URL'] = 'https://play.example/android';
    expect(appDownloadUrl('Mozilla/5.0 (iPhone)')).toBe('https://play.example/android');
  });

  it('is null when neither is configured, so no dead control is drawn', () => {
    expect(appDownloadUrl('Mozilla/5.0 (iPhone)')).toBeNull();
    expect(appDownloadUrl(null)).toBeNull();
  });
});

describe('the language switch’s target URL', () => {
  it('keeps the path and the rest of the query', () => {
    expect(hereWithout('/track', { token: 'abc', ref: 'sms' })).toBe('/track?token=abc&ref=sms');
  });

  it('drops a stale ?lang= so the switch’s own wins', () => {
    // `localeFor` reads the *first* value, so leaving the old one on would switch
    // the reader back to the language they had just left.
    expect(hereWithout('/t/abc', { lang: 'si' })).toBe('/t/abc');
  });

  it('is the bare path when there is no query at all', () => {
    expect(hereWithout('/p/abc', {})).toBe('/p/abc');
  });
});

describe('money and time', () => {
  it('prints whole rupees without cents, and cents when there are any', () => {
    // Everything on the wire is integer minor units (CLAUDE.md). A fare is whole
    // rupees in practice, and `.00` on every amount is noise.
    expect(formatAmountMinor('en', 48_000)).toBe('480');
    expect(formatAmountMinor('en', 48_050)).toBe('480.50');
  });

  it('leaves the rupee mark to the resource string', () => {
    // Where the mark goes relative to the number is a property of the language,
    // not of the amount, so it lives in `web.receipt.paymentAmount` and its two
    // translations.
    expect(formatAmountMinor('si', 48_000)).not.toContain('Rs');
  });

  it('reads an instant in Colombo, not in the container’s UTC', () => {
    // 04:18 UTC is 09:48 in Colombo — the wireframe's own "Handed over at 09:48".
    // Without `timeZone` this would read 04:18, and a late-evening delivery would
    // land on the wrong day.
    expect(formatClock('en', '2026-08-09T04:18:00Z')).toContain('9:48');
    expect(formatInstant('en', '2026-08-09T04:18:00Z')).toContain('9:48');
  });

  it('answers null for an instant that is not one', () => {
    expect(formatClock('en', undefined)).toBeNull();
    expect(formatInstant('en', 'not a date')).toBeNull();
  });

  it('counts the pickup window down as m:ss', () => {
    expect(formatCountdown(278)).toBe('4:38');
    expect(formatCountdown(65)).toBe('1:05');
    expect(formatCountdown(0)).toBe('0:00');
    // A negative remainder is a window that closed while the tab was asleep.
    expect(formatCountdown(-12)).toBe('0:00');
  });
});
