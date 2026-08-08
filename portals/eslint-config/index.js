/**
 * `@mageride/eslint-config` — the shared flat config for every MageRide web
 * surface.
 *
 * Two MageRide rules, both enforcing platform-wide rules that nothing else on
 * the web can enforce:
 *
 *   mageride/no-runtime-css-in-js          AL-52 — Tailwind is the sole styling system
 *   mageride/no-literal-user-facing-strings CLAUDE.md — Si/Ta/En resources, never hardcoded
 *
 * Exports:
 *   `base`   plain TypeScript packages (this config, the preset, i18n)
 *   `react`  anything with JSX — the primitives and the three portals
 */

import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import react from 'eslint-plugin-react';
import reactHooks from 'eslint-plugin-react-hooks';

import noRuntimeCssInJs from './rules/no-runtime-css-in-js.js';
import noLiteralUserFacingStrings from './rules/no-literal-user-facing-strings.js';

/** The MageRide rules, as an ESLint plugin object. */
export const plugin = {
  meta: { name: '@mageride/eslint-config', version: '0.0.0' },
  rules: {
    'no-runtime-css-in-js': noRuntimeCssInJs,
    'no-literal-user-facing-strings': noLiteralUserFacingStrings,
  },
};

/** Build outputs and vendored trees no surface should lint. */
export const ignores = {
  ignores: [
    '**/dist/**',
    '**/node_modules/**',
    '**/.next/**',
    '**/out/**',
    '**/coverage/**',
    '**/smoke/dist/**',
  ],
};

/**
 * Non-JSX packages. The AL-52 fence is on everywhere — a styling library
 * pulled into a plain module is no more permitted than one pulled into a
 * component.
 */
export const base = [
  ignores,
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: { ...globals.node },
    },
    plugins: { mageride: plugin },
    rules: {
      'mageride/no-runtime-css-in-js': 'error',
      '@typescript-eslint/consistent-type-imports': ['error', { prefer: 'type-imports' }],
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
      'no-console': ['error', { allow: ['warn', 'error'] }],
      eqeqeq: ['error', 'always', { null: 'ignore' }],
    },
  },
];

/**
 * JSX surfaces. Adds the accessibility and hooks rules, and turns on the
 * trilingual-resources rule — the one that only has anything to say where
 * markup is written.
 */
export const reactConfig = [
  ...base,
  {
    files: ['**/*.{jsx,tsx}'],
    languageOptions: {
      globals: { ...globals.browser },
      parserOptions: { ecmaFeatures: { jsx: true } },
    },
    settings: { react: { version: 'detect' } },
    plugins: {
      react,
      'react-hooks': reactHooks,
      'jsx-a11y': jsxA11y,
    },
    rules: {
      ...react.configs.flat.recommended.rules,
      ...react.configs.flat['jsx-runtime'].rules,
      ...reactHooks.configs.recommended.rules,
      ...jsxA11y.flatConfigs.recommended.rules,
      'mageride/no-literal-user-facing-strings': 'error',
    },
  },
  {
    // Test fixtures are not shipped copy. A test that renders a component and
    // then looks for its label has to write that label somewhere, and routing
    // it through a resource file would only prove the resource file works.
    // The AL-52 fence stays on — a styling library imported from a test is
    // still a styling library in the tree.
    files: ['**/test/**', '**/tests/**', '**/*.test.{js,jsx,ts,tsx}', '**/*.spec.{js,jsx,ts,tsx}'],
    rules: { 'mageride/no-literal-user-facing-strings': 'off' },
  },
];

export { reactConfig as react };
export default base;
