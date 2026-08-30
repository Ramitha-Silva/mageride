/**
 * The DOM ids the chapter page and its table of contents agree on.
 *
 * A module of its own, with **no `'use client'`**, because the two callers sit on
 * opposite sides of the boundary: `ChapterBody` is a client component (it owns the
 * lightbox) and puts the id on each step, while `ChapterPage` is a server component
 * and builds the TOC links that point at them. Exporting this from `ChapterBody`
 * compiled and then failed the build at export time — *"Attempted to call stepId()
 * from the server but stepId is on the client"* — because a value exported from a
 * `'use client'` module is a client reference, not a function the server may call.
 *
 * Shared so the anchor and its target cannot drift: a TOC that points at
 * `#step-3` while the step renders `id="s3"` is a link that silently does nothing.
 */
export function stepId(index: number): string {
  return `step-${index + 1}`;
}
