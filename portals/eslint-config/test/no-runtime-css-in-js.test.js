/**
 * AL-52 fence — the rule that keeps runtime CSS-in-JS and pre-styled component
 * kits out of the web surfaces.
 *
 * The banned names appear here because a rule test has to state what it bans;
 * `scripts/check-al52.mjs` skips this file for the same reason it skips the
 * rule and the list.
 */

import { RuleTester } from 'eslint';
import { describe, it } from 'vitest';

import rule from '../rules/no-runtime-css-in-js.js';

RuleTester.describe = describe;
RuleTester.it = it;

const ruleTester = new RuleTester({
  languageOptions: {
    ecmaVersion: 'latest',
    sourceType: 'module',
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
});

ruleTester.run('no-runtime-css-in-js', rule, {
  valid: [
    // What AL-52 permits: Tailwind, and headless primitives styled with it.
    { code: "import { Dialog } from 'radix-ui';" },
    { code: "import { Combobox } from '@headlessui/react';" },
    { code: "import { cx } from '@mageride/ui';" },
    { code: "import tokens from '@mageride/tailwind-preset';" },
    { code: "import clsx from 'clsx';" },
    // A local module whose name merely resembles one on the list.
    { code: "import theme from './styled-components-migration-notes.js';" },
    // A plain <style> element is a build-time stylesheet, not styled-jsx.
    { code: 'const el = <style>{css}</style>;' },
  ],

  invalid: [
    {
      code: "import styled from 'styled-components';",
      errors: [{ messageId: 'bannedImport' }],
    },
    {
      code: "import { css } from '@emotion/react';",
      errors: [{ messageId: 'bannedImport' }],
    },
    {
      code: "import Button from '@mui/material/Button';",
      errors: [{ messageId: 'bannedImport' }],
    },
    {
      code: "import 'bootstrap/dist/css/bootstrap.min.css';",
      errors: [{ messageId: 'bannedImport' }],
    },
    {
      code: "export { Box } from '@mui/system';",
      errors: [{ messageId: 'bannedImport' }],
    },
    {
      code: "const s = require('styled-components');",
      errors: [{ messageId: 'bannedImport' }],
    },
    {
      code: "const load = () => import('@stitches/react');",
      errors: [{ messageId: 'bannedImport' }],
    },
    {
      // styled-jsx ships inside Next.js, so the styling can appear with no
      // import to catch.
      code: 'const el = <style jsx>{`.a { color: red }`}</style>;',
      errors: [{ messageId: 'styledJsx' }],
    },
  ],
});
