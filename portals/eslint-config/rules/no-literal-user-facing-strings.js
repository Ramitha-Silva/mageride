/**
 * "Trilingual resources. All user-facing strings must support Si (Sinhala),
 * Ta (Tamil), En (English). Use resource files, never hardcode strings."
 * — CLAUDE.md, Universal Rules.
 *
 * A hardcoded string is not a styling bug or a typo; it is a string that
 * physically cannot be Sinhala for a Sinhala user, and no amount of later
 * translation work reaches it. So it is caught where it is written.
 *
 * Three shapes are flagged:
 *   - JSX text — `<p>Confirm booking</p>`
 *   - a literal child — `<p>{'Confirm booking'}</p>`, which is the same string
 *     with a pair of braces around it
 *   - a literal in a user-facing JSX attribute — `alt`, `title`, `placeholder`,
 *     `aria-label`, … Everything else (`className`, `href`, `data-*`, `id`) is
 *     invisible to a reader and is left alone.
 *
 * `t('booking.confirm')` is an expression, not a literal, so the fixed form
 * passes without an exemption.
 *
 * Options: `{ attributes: [...], allowPattern: '<regex source>' }` — `attributes`
 * replaces the default attribute set, `allowPattern` exempts matching text
 * (a project might exempt a brand name that is identical in all three
 * languages, for instance).
 */

const DEFAULT_ATTRIBUTES = [
  'alt',
  'title',
  'placeholder',
  'label',
  'aria-label',
  'aria-description',
  'aria-placeholder',
  'aria-roledescription',
  'aria-valuetext',
];

/**
 * Whether a run of text would read as words to a user. Punctuation, digits,
 * currency marks and separators carry no language and are not resources —
 * "—", "1,250", "·", "%" all pass.
 *
 * @param {string} value
 */
function isProse(value) {
  return /\p{L}/u.test(value);
}

/** @type {import('eslint').Rule.RuleModule} */
export default {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Ban literal user-facing strings in JSX — every one must come from the si/ta/en resource files (@mageride/i18n).',
    },
    schema: [
      {
        type: 'object',
        properties: {
          attributes: { type: 'array', items: { type: 'string' }, uniqueItems: true },
          allowPattern: { type: 'string' },
        },
        additionalProperties: false,
      },
    ],
    messages: {
      literalText:
        'Literal user-facing text "{{text}}". Move it into the si/ta/en resource files and render it through the translator — CLAUDE.md, Trilingual resources.',
      literalAttribute:
        'Literal user-facing text in `{{attribute}}`: "{{text}}". Move it into the si/ta/en resource files and render it through the translator — CLAUDE.md, Trilingual resources.',
    },
  },

  create(context) {
    const options = context.options[0] ?? {};
    const attributes = new Set(options.attributes ?? DEFAULT_ATTRIBUTES);
    const allow = options.allowPattern ? new RegExp(options.allowPattern, 'u') : null;

    /** @param {string} value */
    function reportable(value) {
      const text = value.trim();
      if (!text || !isProse(text)) return null;
      if (allow?.test(text)) return null;
      return text;
    }

    /** Truncates for the message so a paragraph does not become the error. */
    function clip(text) {
      return text.length > 40 ? `${text.slice(0, 40)}…` : text;
    }

    /** The string an expression spells out, or null if it is not a fixed string. */
    function fixedString(expression) {
      if (expression.type === 'Literal' && typeof expression.value === 'string') {
        return expression.value;
      }
      if (expression.type === 'TemplateLiteral' && expression.expressions.length === 0) {
        return expression.quasis.map((q) => q.value.cooked ?? '').join('');
      }
      return null;
    }

    return {
      JSXText(node) {
        const text = reportable(node.value);
        if (text) context.report({ node, messageId: 'literalText', data: { text: clip(text) } });
      },

      JSXExpressionContainer(node) {
        // Only as a child. As an attribute value it is the JSXAttribute
        // visitor's business, which knows which attributes a reader can see.
        const parentType = node.parent?.type;
        if (parentType !== 'JSXElement' && parentType !== 'JSXFragment') return;

        const raw = fixedString(node.expression);
        if (raw === null) return;

        const text = reportable(raw);
        if (text) context.report({ node, messageId: 'literalText', data: { text: clip(text) } });
      },

      JSXAttribute(node) {
        if (node.name.type !== 'JSXIdentifier' && node.name.type !== 'JSXNamespacedName') return;
        const attribute =
          node.name.type === 'JSXIdentifier'
            ? node.name.name
            : `${node.name.namespace.name}:${node.name.name.name}`;
        if (!attributes.has(attribute)) return;

        const value = node.value;
        if (!value) return;

        const raw =
          value.type === 'JSXExpressionContainer'
            ? fixedString(value.expression)
            : fixedString(value);
        if (raw === null) return;

        const text = reportable(raw);
        if (text) {
          context.report({
            node: value,
            messageId: 'literalAttribute',
            data: { attribute, text: clip(text) },
          });
        }
      },
    };
  },
};
