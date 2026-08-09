import { describe, expect, it } from 'vitest';

import { isDeadToken, localProblem, problemMessageKey, ProblemError } from '@/api/problem';
import { isWellFormedToken, trackPath } from '@/api/track';

/**
 * The token is the credential, so what this surface does with a *malformed* one is
 * a security property rather than a validation nicety.
 *
 * `ShareTokenMinter` produces 32 random bytes as base64url (43 characters) and
 * `public-bff.yaml` bounds the path parameter at 16–128, so anything outside
 * `[A-Za-z0-9_-]` was never minted. Refusing it here does three things: a junk link
 * becomes SCR-WT-006 without a round trip, a probe cannot spend a real visitor's
 * per-IP rate budget on this deployment's behalf, and — the one that matters — a
 * value that cannot contain `/`, `?`, `#` or a percent sequence cannot become a
 * path of its own when it is interpolated into a URL.
 */

describe('a token that could have been minted', () => {
  it('accepts a base64url value of the length notification-svc mints', () => {
    // 32 bytes, base64url, no padding — what `ShareTokenMinter` writes into the SMS.
    expect(isWellFormedToken('JQnQ4KcVsE9mR7tYuI0pLzXwBvNa1234-_x')).toBe(true);
  });

  it('refuses a value shorter than any token the platform issues', () => {
    expect(isWellFormedToken('short')).toBe(false);
  });

  it('refuses a value longer than the contract admits', () => {
    expect(isWellFormedToken('a'.repeat(129))).toBe(false);
  });

  it('refuses nothing at all', () => {
    expect(isWellFormedToken(undefined)).toBe(false);
    expect(isWellFormedToken(null)).toBe(false);
    expect(isWellFormedToken('')).toBe(false);
  });

  it.each([
    ['a path separator', 'aaaaaaaaaaaaaaaa/../admin'],
    ['a query string', 'aaaaaaaaaaaaaaaa?scope=package'],
    ['a fragment', 'aaaaaaaaaaaaaaaa#top'],
    ['a percent escape', 'aaaaaaaaaaaaaaaa%2f%2e%2e'],
    ['a whole URL', 'https://example.invalid/public/track/x'],
    ['whitespace', 'aaaaaaaaaaaaaaaa aaaa'],
  ])('refuses %s', (_what, value) => {
    expect(isWellFormedToken(value)).toBe(false);
  });
});

describe('the one place a token becomes a URL', () => {
  const token = 'JQnQ4KcVsE9mR7tYuI0pLzXwBvNa1234';

  it('addresses the /public/track family and nothing else', () => {
    expect(trackPath(token)).toBe(`/public/track/${token}`);
    expect(trackPath(token, '/live')).toBe(`/public/track/${token}/live`);
    expect(trackPath(token, '/pickup/decline')).toBe(`/public/track/${token}/pickup/decline`);
  });

  it('throws the family’s own 404 for a value that was never a token', () => {
    // A 404 and not a 400: every screen already routes a dead token to SCR-WT-006,
    // and "this was never a token" belongs on the same page as "this token is over".
    // Telling the two apart would make the surface an oracle over which links exist.
    expect(() => trackPath('../rides/9')).toThrowError(ProblemError);
    try {
      trackPath('../rides/9');
    } catch (error) {
      expect((error as ProblemError).status).toBe(404);
      expect((error as ProblemError).code).toBe('token-unknown');
    }
  });
});

describe('a dead token is a screen, not a failure', () => {
  it('treats 404 and 410 as the dead end', () => {
    expect(isDeadToken(new ProblemError(localProblem('token-unknown', 404, '/public/track')))).toBe(
      true,
    );
    expect(
      isDeadToken(new ProblemError(localProblem('token-expired-or-revoked', 410, '/public/track'))),
    ).toBe(true);
  });

  it('does not treat a rate limit or an outage as an expired link', () => {
    // Sending somebody to "ask the sender for a new link" because the platform is
    // busy would have them chase a link that would fail in exactly the same way.
    expect(isDeadToken(new ProblemError(localProblem('rate-limited', 429, '/x')))).toBe(false);
    expect(isDeadToken(new ProblemError(localProblem('service-unavailable', 503, '/x')))).toBe(false);
    expect(isDeadToken(new Error('boom'))).toBe(false);
  });
});

describe('what a failure is shown as', () => {
  it('never renders the developer-facing English title', () => {
    // `_shared.yaml`: "Short English summary for developers. Never localised."
    const problem = {
      type: 'https://mageride.lk/errors/rate-limited',
      title: 'Too many requests',
      status: 429,
    };

    expect(problemMessageKey(problem)).toBe('web.error.rateLimited');
    expect(problemMessageKey(problem)).not.toContain(problem.title);
  });

  it('falls back to one sentence for a code it has never seen', () => {
    expect(
      problemMessageKey({ type: 'https://mageride.lk/errors/tea-pot', title: 'x', status: 418 }),
    ).toBe('web.error.unexpected');
  });
});
