import { createWwwTranslator, HREFLANG, WWW_LOCALES, type Locale } from '@/i18n';
import { href, routesInGroup } from '@/lib/routes';

import type { HeaderLabels } from './Header';
import type { NavLink } from './NavLink';

/**
 * Builds every string the header and its three children render — **on the server,
 * which is the whole point of MCS-36 D3.**
 *
 * ## Why this file exists at all
 *
 * `Header`, `LocaleSwitcher`, `ThemeToggle` and `MobileMenu` are all client
 * components: they need `usePathname`, `useState` and a `MutationObserver`. Until D3
 * each one took a `locale` and called `createWwwTranslator` itself, which is a
 * pleasant API and put **~88 kB gzipped of resource tables into every page's
 * bundle** — the header is in the shared layout, so it was every page, including
 * pages with nothing to translate on them.
 *
 * The strings are resolved here instead, once, by the server. What crosses the
 * boundary is a plain object; the tables stay where they were read.
 *
 * ## Why it is not simply "props instead of a locale"
 *
 * `portals/www/CLAUDE.md` used to refuse this, and the objection was fair: *"thirty
 * strings is not a prop list."* It is not thirty props — it is **one**, shaped and
 * typed, and TypeScript refuses a call that forgets a field. That keeps the property
 * the resource table itself has: a missing string is a compile error, not a blank
 * `aria-label` somebody meets in a screen reader months later.
 *
 * The route links are the part that would genuinely have been thirty props. They are
 * a list, built from the same `routesInGroup` the nav has always read, with `href`
 * resolved and `path` kept — because "am I the current page?" is a `usePathname()`
 * question that only the client can answer.
 */
export function headerLabels(locale: Locale): HeaderLabels {
  const t = createWwwTranslator(locale);

  const link = (route: { path: string; labelKey: Parameters<typeof t>[0] }): NavLink => ({
    path: route.path,
    href: href(locale, route.path),
    label: t(route.labelKey),
  });

  return {
    brandHome: t('www.nav.brandHome'),
    brandName: t('www.brand.name'),
    primaryNav: t('www.nav.primary'),
    homeHref: href(locale, ''),
    links: routesInGroup('primary').map(link),

    localeSwitcher: {
      label: t('www.language.label'),
      /*
       * Every published locale, with its endonym and its own BCP-47 tag. The
       * `current` flag is decided here rather than in the component: the server knows
       * which locale it is rendering, and passing the answer is one less thing for a
       * client component to hold a `locale` for.
       */
      options: WWW_LOCALES.map((candidate) => {
        const language = t(`language.${candidate}`);
        const current = candidate === locale;
        return {
          locale: candidate,
          hrefLang: HREFLANG[candidate],
          language,
          ariaLabel: current
            ? t('www.language.current', { language })
            : t('www.language.switchTo', { language }),
          current,
        };
      }),
    },

    themeToggle: {
      toggle: t('www.appearance.dark'),
    },

    mobileMenu: {
      open: t('www.nav.menu.open'),
      title: t('www.nav.menu.title'),
      close: t('www.nav.menu.close'),
      primaryNav: t('www.nav.primary'),
      // Primary *and* support, in that order — the dialog carries the whole site,
      // where the wide-viewport nav carries only the primary group.
      links: [...routesInGroup('primary'), ...routesInGroup('support')].map(link),
    },
  };
}
