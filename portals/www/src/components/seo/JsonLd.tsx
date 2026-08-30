import type { JsonLdNode } from '@/lib/json-ld';

/**
 * Renders one or more JSON-LD blocks into the document.
 *
 * A server component with no state, no effect and no hydration cost — the script
 * element is inert markup and nothing on the client ever reads it.
 *
 * ## The escaping is not paranoia
 *
 * `<script>` content is CDATA-ish: the parser ends the element at the first
 * `</script` regardless of JSON quoting. Every string in these blocks comes from a
 * **translated resource**, and a translator working in a spreadsheet can put
 * anything in one — so escaping `<` is the difference between a structured-data
 * block and an XSS hole that arrives through the translation memory rather than
 * through a request.
 *
 * `<` is the JSON escape for `<`, so the value a parser reads back is
 * unchanged; only the bytes in the document differ.
 *
 * ## Why `dangerouslySetInnerHTML` is correct here
 *
 * React would otherwise HTML-escape the JSON into `&quot;` entities, which a
 * structured-data parser reading `textContent` does *not* decode — the block would
 * be present and unparseable. This is the documented way to emit JSON-LD, and the
 * escape above is what makes it safe.
 */
export function JsonLd({ nodes }: { readonly nodes: readonly JsonLdNode[] }) {
  return (
    <>
      {nodes.map((node, index) => (
        <script
          // The list is fixed per page and derived from registries; there is no
          // stable id to key on and no reordering that could occur.
          key={index}
          type="application/ld+json"
          dangerouslySetInnerHTML={{ __html: serialize(node) }}
        />
      ))}
    </>
  );
}

export function serialize(node: JsonLdNode): string {
  return JSON.stringify(node).replaceAll('<', '\\u003c');
}
