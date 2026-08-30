'use client';

import { useEffect } from 'react';

/**
 * Opens the `<details>` a `#faq-<id>` link names, and scrolls to it.
 *
 * S18 asks for the FAQ to be deep-linkable. Everything else on that page is native
 * `<details>` with no script at all (`./FaqAccordion`), and this is the one clause
 * the platform does not cover: browsers auto-expand a closed `<details>` when a
 * fragment points at something **inside** it, but `#faq-coverage` names the
 * `<details>` element itself, which is already the scroll target and stays shut.
 *
 * So this is progressive enhancement over a page that is complete without it. With
 * no JavaScript, `#faq-coverage` scrolls the reader to the right question with the
 * answer already in the markup one press away — a degradation, not a broken link.
 *
 * Three details worth keeping:
 *
 *   - **`hashchange` as well as mount.** A link from one answer to another on the
 *     same page changes the hash without remounting anything, and the browser will
 *     not re-run the fragment behaviour for a target that is already the target.
 *   - **`scrollIntoView` after opening, not before.** Opening a `<details>` grows
 *     the document, so a scroll computed first lands short by the height of every
 *     answer above it.
 *   - **`decodeURIComponent`, because the ids are not all plain ASCII-safe.** They
 *     are today; a pasted URL that has been through a chat client may not be.
 *
 * Renders nothing. It is mounted by `app/[locale]/faq/page.tsx` beside the
 * accordion rather than wrapping it, so the accordion stays a server component and
 * no answer crosses a hydration boundary.
 */
export function FaqHashOpener() {
  useEffect(() => {
    const open = () => {
      if (!window.location.hash.startsWith('#faq-')) return;

      let id: string;
      try {
        id = decodeURIComponent(window.location.hash.slice(1));
      } catch {
        return;
      }

      const target = document.getElementById(id);
      if (!(target instanceof HTMLDetailsElement)) return;

      target.open = true;
      target.scrollIntoView({ block: 'start' });
    };

    open();
    window.addEventListener('hashchange', open);
    return () => window.removeEventListener('hashchange', open);
  }, []);

  return null;
}
