import { describe, expect, it } from 'vitest';

import { errorCode, localProblem, ProblemError, problemMessageKey, readProblem } from '@/api/problem';
import { adminEn } from '@/i18n/messages/en';

/**
 * `application/problem+json` handling — D3' §0 makes it the shape of every
 * MageRide error, so this is the shape of every failure the console renders.
 */

function response(body: unknown, init: ResponseInit & { json?: boolean } = {}): Response {
  const { json = true, ...rest } = init;
  return new Response(typeof body === 'string' ? body : JSON.stringify(body), {
    ...rest,
    headers: json ? { 'content-type': 'application/problem+json' } : { 'content-type': 'text/html' },
  });
}

describe('reading a problem', () => {
  it('parses the code out of the type URI', () => {
    expect(errorCode({ type: 'https://mageride.lk/errors/offer-expired', title: '', status: 409 })).toBe(
      'offer-expired',
    );
  });

  it('answers `unknown` for a type that is not the MageRide registry', () => {
    expect(errorCode({ type: 'about:blank', title: '', status: 400 })).toBe('unknown');
    expect(errorCode({ type: '', title: '', status: 400 })).toBe('unknown');
  });

  it('keeps the transport status when the body disagrees with it', async () => {
    const problem = await readProblem(
      response({ type: 'https://mageride.lk/errors/conflict', title: 'Conflict', status: 200 }, { status: 409 }),
      '/v1/admin/trains',
    );

    expect(problem.status).toBe(409);
  });

  it('carries the 423 lock-out extension through', async () => {
    const problem = await readProblem(
      response(
        {
          type: 'https://mageride.lk/errors/otp-locked',
          title: 'Locked',
          status: 423,
          retryAfterSeconds: 754,
        },
        { status: 423 },
      ),
      '/v1/admin/auth/login',
    );

    expect(problem.retryAfterSeconds).toBe(754);
  });

  it('synthesises a problem from a gateway response that is not one', async () => {
    // A 502 with an HTML body is not something the services produced, and the
    // alternative to inventing a problem here is a JSON parse error on somebody's
    // screen.
    const problem = await readProblem(response('<html>bad gateway</html>', { status: 502, json: false }), '/v1/admin/session');

    expect(problem.status).toBe(502);
    expect(errorCode(problem)).toBe('internal-error');
    expect(problem.instance).toBe('/v1/admin/session');
  });

  it('survives a body that claims to be JSON and is not', async () => {
    const problem = await readProblem(response('{ not json', { status: 500 }), '/v1/admin/session');
    expect(problem.status).toBe(500);
  });
});

describe('rendering a problem', () => {
  it('maps every code the shell can produce to a resource key that exists', () => {
    const codes = [
      'unauthorized',
      'forbidden',
      'not-found',
      'validation-failed',
      'bad-request',
      'conflict',
      'user-blocked',
      'auth-not-found',
      'otp-locked',
      'rate-limited',
      'dependency-unavailable',
      'service-unavailable',
      'upstream-timeout',
      'internal-error',
    ];

    for (const code of codes) {
      const key = problemMessageKey(localProblem(code, 400, '/v1/admin/session'));
      expect(Object.hasOwn(adminEn, key), `${code} → ${key}`).toBe(true);
    }
  });

  it('falls back to the unexpected-error key for a code it has never seen', () => {
    expect(problemMessageKey(localProblem('offer-expired', 409, '/x'))).toBe('admin.error.unexpected');
    expect(problemMessageKey({ type: 'about:blank', title: 'Whatever', status: 500 })).toBe(
      'admin.error.unexpected',
    );
  });

  it('never routes the English title into the resource lookup', () => {
    // `_shared.yaml`: "Short English summary for developers. Never localised."
    // The mapping is on the code alone, so a service that changes its wording
    // cannot change what an operator is shown.
    const a = problemMessageKey({
      type: 'https://mageride.lk/errors/forbidden',
      title: 'Forbidden',
      status: 403,
    });
    const b = problemMessageKey({
      type: 'https://mageride.lk/errors/forbidden',
      title: 'Completely different wording',
      status: 403,
    });

    expect(a).toBe(b);
    expect(a).toBe('admin.error.forbidden');
  });
});

describe('ProblemError', () => {
  it('exposes the code, the status and the resource key', () => {
    const error = new ProblemError(localProblem('otp-locked', 423, '/v1/admin/auth/login'));

    expect(error.code).toBe('otp-locked');
    expect(error.status).toBe(423);
    expect(error.messageKey).toBe('admin.error.accountLocked');
    expect(error).toBeInstanceOf(Error);
  });
});
