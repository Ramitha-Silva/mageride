import { describe, expect, it } from 'vitest';

import { oauthStateCookieOptions, sessionCookieOptions } from '@/server/cookies';
import {
  authorizeUrl,
  decodeState,
  encodeState,
  isProviderId,
  PROVIDER_IDS,
  stateMatches,
} from '@/server/oauth';
import { safeNextPath } from '@/server/next-path';

/**
 * AL-07's two federated arms, and the three properties that make them safe:
 * the provider is asked for an **ID token** and nothing this portal could redeem,
 * the state cookie survives the cross-site POST the flow returns as, and a
 * returned `state` that does not match the stored nonce is refused.
 */

const CONFIG = {
  clientId: 'client-id.apps.example',
  redirectUri: 'https://fleet.mageride.lk/auth/callback/google',
};

describe('the authorize URL', () => {
  it('asks each provider for an identity token by form post', () => {
    for (const provider of PROVIDER_IDS) {
      const url = new URL(authorizeUrl(provider, CONFIG, 'nonce-value'));

      expect(url.searchParams.get('response_mode'), provider).toBe('form_post');
      expect(url.searchParams.get('response_type'), provider).toContain('id_token');
      expect(url.searchParams.get('client_id'), provider).toBe(CONFIG.clientId);
      expect(url.searchParams.get('redirect_uri'), provider).toBe(CONFIG.redirectUri);
      expect(url.searchParams.get('state'), provider).toBe('nonce-value');
      expect(url.searchParams.get('nonce'), provider).toBe('nonce-value');
    }
  });

  it('asks Apple for `code id_token`, which is the only shape that yields one', () => {
    const apple = new URL(authorizeUrl('apple', CONFIG, 'n'));
    expect(apple.searchParams.get('response_type')).toBe('code id_token');
    expect(apple.origin).toBe('https://appleid.apple.com');
  });

  it('forces Google’s account chooser, because fleet offices share machines', () => {
    const google = new URL(authorizeUrl('google', CONFIG, 'n'));
    expect(google.searchParams.get('prompt')).toBe('select_account');
    expect(google.origin).toBe('https://accounts.google.com');
  });

  it('never asks for anything this portal could exchange for a token itself', () => {
    for (const provider of PROVIDER_IDS) {
      const url = authorizeUrl(provider, CONFIG, 'n');
      expect(url, provider).not.toContain('client_secret');
      expect(url, provider).not.toContain('access_type=offline');
    }
  });

  it('accepts the two provider ids and nothing else', () => {
    expect(isProviderId('google')).toBe(true);
    expect(isProviderId('apple')).toBe(true);
    expect(isProviderId('facebook')).toBe(false);
    expect(isProviderId('../admin')).toBe(false);
  });
});

describe('the state cookie', () => {
  it('is SameSite=None over HTTPS, or the form_post would arrive without it', () => {
    // A `Lax` cookie is sent on top-level GET navigations and not on a cross-site
    // POST — which is exactly what both providers send back. This is the one
    // cookie on the portal that is not Lax, and the reason is mechanical.
    const secure = oauthStateCookieOptions(true, 600);
    expect(secure.sameSite).toBe('none');
    expect(secure.secure).toBe(true);
    expect(secure.httpOnly).toBe(true);
    expect(secure.maxAge).toBe(600);
  });

  it('falls back to Lax without Secure, because browsers reject None on its own', () => {
    expect(oauthStateCookieOptions(false, 600).sameSite).toBe('lax');
  });

  it('is the only cookie that is not Lax', () => {
    expect(sessionCookieOptions(true, 60).sameSite).toBe('lax');
    expect(sessionCookieOptions(true, 60).httpOnly).toBe(true);
  });

  it('round-trips the nonce and the return path, and refuses a tampered value', () => {
    const encoded = encodeState({ nonce: 'abc', next: '/vehicles' });
    expect(decodeState(encoded)).toEqual({ nonce: 'abc', next: '/vehicles' });

    expect(decodeState(undefined)).toBeNull();
    expect(decodeState('not-base64url!!')).toBeNull();
    expect(decodeState(Buffer.from('{}', 'utf8').toString('base64url'))).toBeNull();
  });
});

describe('the state check', () => {
  it('matches only an exact nonce', () => {
    expect(stateMatches('abc', 'abc')).toBe(true);
    expect(stateMatches('abd', 'abc')).toBe(false);
    expect(stateMatches('ab', 'abc')).toBe(false);
    expect(stateMatches('', 'abc')).toBe(false);
    expect(stateMatches(null, 'abc')).toBe(false);
    expect(stateMatches(undefined, 'abc')).toBe(false);
  });
});

describe('the return path is never an open redirect', () => {
  it('accepts a path on this origin and refuses everything else', () => {
    expect(safeNextPath('/vehicles')).toBe('/vehicles');
    expect(safeNextPath('//evil.example')).toBeNull();
    expect(safeNextPath('/\\evil.example')).toBeNull();
    expect(safeNextPath('https://evil.example')).toBeNull();
    expect(safeNextPath('/')).toBeNull();
    expect(safeNextPath(null)).toBeNull();
  });
});
