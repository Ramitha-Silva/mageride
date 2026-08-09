import { problemMessageKey, type ProblemDetails } from '@/api/problem';
import type { WebTranslator } from '@/i18n';

/**
 * How an `application/problem+json` failure is put in front of somebody who does
 * not have a MageRide account.
 *
 * The same three rules the other two portals hold, and the reason is stronger
 * here: the reader is a package recipient or a rider, not an operator, and there
 * is no support desk they can quote a stack trace to.
 *
 *  - **`title` is never rendered.** `_shared.yaml` says it in as many words —
 *    "Short English summary for developers. Never localised" — so a panel that
 *    showed it would be the one thing on this surface that speaks English to every
 *    reader in the country, and it would pass review because the string is not in
 *    the source.
 *  - **`detail` is not rendered either.** It is diagnostics, in English, aimed at
 *    whoever is reading the log.
 *  - **`traceId` is rendered verbatim, in every language.** It is the only handle
 *    support has, and translating an identifier would be worse than useless.
 *
 * A dead token never reaches here: `isDeadToken` sends it to SCR-WT-006, because
 * an expired link is a *screen*, not a failure the reader did something about.
 */
export function ProblemPanel({ t, problem }: { t: WebTranslator; problem: ProblemDetails }) {
  return (
    <div
      role="alert"
      className="flex flex-col gap-xxs rounded-md border border-error/40 bg-error/10 p-sm text-start"
    >
      <p className="text-body-sm font-semibold text-error">{t('web.error.title')}</p>
      <p className="text-body-sm text-on-surface">{t(problemMessageKey(problem))}</p>
      {problem.traceId ? (
        <p className="text-caption text-on-surface-variant">
          {t('web.error.reference', { traceId: problem.traceId })}
        </p>
      ) : null}
    </div>
  );
}
