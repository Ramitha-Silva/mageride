import { problemMessageKey, type ProblemDetails } from '@/api/problem';
import { getTranslator } from '@/i18n/server';

/**
 * How a `application/problem+json` failure is put in front of an operator.
 *
 * Three rules, and each is a thing the obvious implementation gets wrong:
 *
 *  - **`title` is never rendered.** `_shared.yaml` says it in as many words —
 *    "Short English summary for developers. Never localised" — so a panel that
 *    showed it would be the one control on the platform that speaks English to
 *    every user in the country, and it would pass review because the string is
 *    not in the source. The `type` URI's kebab code is the message; the resource
 *    file is where the sentence lives.
 *  - **`detail` is not rendered either.** The contract promises it contains no
 *    PII, not that it is copy — it is diagnostics, in English, aimed at whoever
 *    is reading the log.
 *  - **`traceId` is rendered verbatim, in every language.** It is the only handle
 *    support has, and translating an identifier would be worse than useless.
 *
 * Field-level `errors` are deliberately not expanded here: `validation-failed`
 * keys them by field name, and a field name only means something to the form that
 * has that field. A screen renders them on its own controls.
 */
export async function ProblemPanel({ problem }: { problem: ProblemDetails }) {
  const t = await getTranslator();

  return (
    <div
      role="alert"
      className="flex flex-col gap-xxs rounded-md border border-error/40 bg-error/10 p-sm"
    >
      <p className="text-body-sm font-semibold text-error">{t('admin.error.title')}</p>
      <p className="text-body-sm text-on-surface">{t(problemMessageKey(problem))}</p>
      {problem.traceId ? (
        <p className="text-caption text-on-surface-variant">
          {t('admin.error.reference', { traceId: problem.traceId })}
        </p>
      ) : null}
    </div>
  );
}
