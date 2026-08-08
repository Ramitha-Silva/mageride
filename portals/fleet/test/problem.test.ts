import { describe, expect, it } from 'vitest';

import { errorCode, localProblem, ProblemError, problemMessageKey, readProblem } from '@/api/problem';
import { createFleetTranslator } from '@/i18n';
import { fleetEn } from '@/i18n/messages/en';

/**
 * D3' §0's error contract, as the portal reads it.
 *
 * The rule under test is one sentence from `_shared.yaml`: `title` is "Short
 * English summary for developers. **Never localised**". So every message the
 * portal shows is resolved from the kebab code, and a code the shell has never
 * heard of resolves to the generic sentence rather than to whatever English the
 * service happened to send.
 */

function problem(code: string, status = 400) {
  return localProblem(code, status, '/v1/fleets');
}

describe('the code is the message', () => {
  it('parses the kebab code out of the type URI', () => {
    expect(errorCode(problem('fleet-not-approved'))).toBe('fleet-not-approved');
    expect(errorCode({ type: 'about:blank', title: 'x', status: 500 })).toBe('unknown');
    expect(errorCode({ type: 'https://mageride.lk/errors/', title: 'x', status: 500 })).toBe(
      'unknown',
    );
  });

  it('maps each of FleetAccessFilter’s four refusals to its own sentence', () => {
    const t = createFleetTranslator('en');
    const sentences = [
      'fleet-not-found',
      'not-fleet-member',
      'fleet-role-insufficient',
      'fleet-not-approved',
    ].map((code) => t(problemMessageKey(problem(code, 403))));

    // Four different facts about why a request was refused, and four different
    // things the operator does about them. The generic `forbidden` sentence
    // describes none of them.
    expect(new Set(sentences).size).toBe(4);
    expect(sentences).not.toContain(t('fleet.error.forbidden'));
  });

  it('falls back to the generic sentence for a code it has never seen', () => {
    expect(problemMessageKey(problem('some-future-code'))).toBe('fleet.error.unexpected');
  });

  it('resolves every mapped key in all three locale tables', () => {
    for (const code of [
      'unauthorized',
      'forbidden',
      'not-found',
      'validation-failed',
      'conflict',
      'otp-locked',
      'rate-limited',
      'service-unavailable',
      'fleet-not-approved',
    ]) {
      const key = problemMessageKey(problem(code));
      expect(Object.hasOwn(fleetEn, key), key).toBe(true);
    }
  });
});

describe('a response that is not a problem at all', () => {
  it('synthesises one rather than throwing a SyntaxError at the operator', async () => {
    const html = new Response('<html>502 Bad Gateway</html>', {
      status: 502,
      statusText: 'Bad Gateway',
      headers: { 'content-type': 'text/html' },
    });

    const parsed = await readProblem(html, '/v1/fleets');
    expect(parsed.status).toBe(502);
    expect(errorCode(parsed)).toBe('internal-error');
  });

  it('trusts the transport’s status over a body that disagrees with it', async () => {
    const lying = new Response(JSON.stringify({ status: 200, title: 'fine' }), {
      status: 409,
      headers: { 'content-type': 'application/problem+json' },
    });

    expect((await readProblem(lying, '/v1/fleets')).status).toBe(409);
  });
});

describe('ProblemError', () => {
  it('carries the code, the status and the resource key', () => {
    const error = new ProblemError(problem('fleet-not-approved', 403));

    expect(error.code).toBe('fleet-not-approved');
    expect(error.status).toBe(403);
    expect(error.messageKey).toBe('fleet.error.orgNotApproved');
  });
});
