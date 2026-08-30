/**
 * A pass-through, and the only file in this application that is not what it looks
 * like.
 *
 * Next requires a root layout above `app/page.tsx`, and the **real** root layout —
 * the one that emits `<html>` and `<body>` — is `app/[locale]/layout.tsx`, because
 * `<html lang>` on this surface is the path segment (A32's `hreflang` needs the
 * locale to be part of a canonical URL, so it cannot be a header read the way it is
 * on `web-passenger`). Two layouts emitting `<html>` would be two documents.
 *
 * So this one emits nothing. The only page it ever wraps is `app/page.tsx`, which
 * `redirect()`s before it renders anything at all — there is no reader who sees a
 * document produced by this file.
 */
export default function RootLayout({ children }: { children: React.ReactNode }) {
  return children;
}
