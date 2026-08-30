/**
 * Placeholder substitution, with **no message table anywhere near it**.
 *
 * `createWwwTranslator` does two things: it looks a key up in a resource table, and
 * it fills `{placeholders}` in the result. MCS-36 D3 moves the *lookup* to the
 * server, because the table is what costs 88 kB gzipped. The *filling* is nine lines
 * and costs nothing, and one string genuinely needs it on the client: `/screens`
 * renders "Showing 12 of 70", where the count depends on a filter the URL chooses
 * and the server cannot know.
 *
 * The alternative was resolving all 71 possible counts on the server and picking one
 * by index — which works, and is what the lightbox does for its announcements. It is
 * the right trade there (a handful of slides) and the wrong one here (71 strings to
 * avoid shipping nine lines).
 *
 * A missing value leaves the placeholder in the string rather than substituting
 * `undefined`, which is `@mageride/i18n`'s own rule and for its reason:
 * `"Showing {count} of 70"` reaching a reader is a visible bug somebody reports,
 * where `"Showing undefined of 70"` reads like real copy that is merely wrong.
 */
const PLACEHOLDER = /\{(\w+)\}/g;

export function substitute(
  template: string,
  params: Readonly<Record<string, string | number>>,
): string {
  return template.replace(PLACEHOLDER, (match, name: string) => {
    const value = params[name];
    return value === undefined ? match : String(value);
  });
}
