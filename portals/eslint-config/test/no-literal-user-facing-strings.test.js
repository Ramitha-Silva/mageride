/**
 * The trilingual-resources rule (CLAUDE.md, Universal Rules).
 *
 * The interesting cases are the *valid* ones: the rule has to stay quiet about
 * class names, hrefs, numbers and punctuation, or a screen author will turn it
 * off and the guarantee goes with it.
 */

import { RuleTester } from 'eslint';
import { describe, it } from 'vitest';

import rule from '../rules/no-literal-user-facing-strings.js';

RuleTester.describe = describe;
RuleTester.it = it;

const ruleTester = new RuleTester({
  languageOptions: {
    ecmaVersion: 'latest',
    sourceType: 'module',
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
});

ruleTester.run('no-literal-user-facing-strings', rule, {
  valid: [
    // The fixed form.
    { code: 'const el = <p>{t("booking.confirm")}</p>;' },
    { code: 'const el = <img alt={t("vehicle.photo")} src={url} />;' },
    { code: 'const el = <input placeholder={placeholder} />;' },
    // Attributes a reader never sees.
    { code: 'const el = <div className="rounded-sm bg-primary" id="root" data-testid="cta" />;' },
    { code: 'const el = <a href="/finance/reconciliation" target="_blank" />;' },
    // Text carrying no language.
    { code: 'const el = <span>·</span>;' },
    { code: 'const el = <span>—</span>;' },
    { code: 'const el = <span>{" / "}</span>;' },
    { code: 'const el = <td>1,250</td>;' },
    // Whitespace between elements.
    { code: 'const el = <p>\n  {t("a")}\n  {t("b")}\n</p>;' },
    // A braced expression that is not a fixed string.
    { code: 'const el = <p>{count}</p>;' },
    { code: 'const el = <p>{`${count} of ${total}`}</p>;' },
    // Strings outside JSX are not user-facing by inspection — a resource key, a
    // route, a log line. The rule deliberately says nothing about them.
    { code: 'const key = "Confirm booking";' },
    // The escape hatch, for a word identical in all three languages.
    {
      code: 'const el = <p>MageRide</p>;',
      options: [{ allowPattern: '^MageRide$' }],
    },
    // A narrowed attribute set.
    {
      code: 'const el = <img alt="left as-is" />;',
      options: [{ attributes: ['title'] }],
    },
  ],

  invalid: [
    {
      code: 'const el = <p>Confirm booking</p>;',
      errors: [{ messageId: 'literalText', data: { text: 'Confirm booking' } }],
    },
    {
      code: 'const el = <button>Approve</button>;',
      errors: [{ messageId: 'literalText' }],
    },
    {
      // Braces do not make a hardcoded string any less hardcoded.
      code: "const el = <p>{'Confirm booking'}</p>;",
      errors: [{ messageId: 'literalText' }],
    },
    {
      code: 'const el = <p>{`Confirm booking`}</p>;',
      errors: [{ messageId: 'literalText' }],
    },
    {
      // Mixed content: the literal half is still a literal.
      code: 'const el = <p>Total: {amount}</p>;',
      errors: [{ messageId: 'literalText' }],
    },
    {
      code: 'const el = <img alt="Driver licence" src={url} />;',
      errors: [{ messageId: 'literalAttribute' }],
    },
    {
      code: 'const el = <input placeholder="Search drivers" />;',
      errors: [{ messageId: 'literalAttribute' }],
    },
    {
      code: 'const el = <button aria-label="Close dialog" />;',
      errors: [{ messageId: 'literalAttribute' }],
    },
    {
      // A braced literal and a template with no substitutions are the same
      // string with extra syntax.
      code: 'const el = <img alt={"Vehicle photo"} src={url} />;',
      errors: [{ messageId: 'literalAttribute' }],
    },
    {
      code: 'const el = <img alt={`Vehicle photo`} src={url} />;',
      errors: [{ messageId: 'literalAttribute' }],
    },
    {
      // Sinhala and Tamil hardcoded are just as untranslatable as English.
      code: 'const el = <p>තහවුරු කරන්න</p>;',
      errors: [{ messageId: 'literalText' }],
    },
  ],
});
