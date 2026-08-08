/**
 * AL-52 — Tailwind CSS is the sole styling system on the web.
 *
 * Flags any import of a runtime CSS-in-JS library or a pre-styled component kit.
 * The list lives in `banned-styling-packages.json` so the package names are
 * spelled exactly once inside portals/ (see that file's `$comment`).
 *
 * A `<style jsx>` element is flagged too: styled-jsx ships inside Next.js, so
 * the styling can appear without an import to catch.
 */

import banned from '../banned-styling-packages.json' with { type: 'json' };

/** @param {string} specifier */
function violation(specifier) {
  if (banned.packages.includes(specifier)) return specifier;
  const prefix = banned.prefixes.find((p) => specifier.startsWith(p));
  return prefix ? specifier : null;
}

/** @type {import('eslint').Rule.RuleModule} */
export default {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Ban runtime CSS-in-JS and pre-styled component kits — Tailwind CSS is the sole styling system (AL-52).',
    },
    schema: [],
    messages: {
      bannedImport:
        "'{{specifier}}' is excluded by AL-52 — Tailwind CSS is the sole styling system for MageRide web surfaces. Use Tailwind utilities from @mageride/tailwind-preset, and headless primitives (Radix UI / Headless UI) where behaviour is needed.",
      styledJsx:
        '<style jsx> injects CSS at runtime, which AL-52 excludes. Move the rules into Tailwind utilities or a build-time stylesheet.',
    },
  },

  create(context) {
    /**
     * @param {import('estree').Node} node
     * @param {unknown} raw
     */
    function check(node, raw) {
      if (typeof raw !== 'string') return;
      const specifier = violation(raw);
      if (specifier) context.report({ node, messageId: 'bannedImport', data: { specifier } });
    }

    return {
      ImportDeclaration(node) {
        check(node, node.source.value);
      },
      ExportNamedDeclaration(node) {
        if (node.source) check(node, node.source.value);
      },
      ExportAllDeclaration(node) {
        if (node.source) check(node, node.source.value);
      },
      ImportExpression(node) {
        if (node.source.type === 'Literal') check(node, node.source.value);
      },
      CallExpression(node) {
        const isRequire = node.callee.type === 'Identifier' && node.callee.name === 'require';
        const [first] = node.arguments;
        if (isRequire && first && first.type === 'Literal') check(node, first.value);
      },
      JSXOpeningElement(node) {
        const name = node.name;
        if (name.type !== 'JSXIdentifier' || name.name !== 'style') return;
        const hasJsx = node.attributes.some(
          (attr) => attr.type === 'JSXAttribute' && attr.name.type === 'JSXIdentifier' && attr.name.name === 'jsx',
        );
        if (hasJsx) context.report({ node, messageId: 'styledJsx' });
      },
    };
  },
};
