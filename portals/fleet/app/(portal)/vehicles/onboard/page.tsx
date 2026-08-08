import { permanentRedirect } from 'next/navigation';

/**
 * `web_fleet.html`'s own address bar for SCR-FP-004 is
 * `fleet.mageride.lk/vehicles/onboard`, and this is that URL.
 *
 * It is a redirect rather than a second copy of the screen. C111's manifest
 * anticipated it — "`/vehicles/onboard` in the wireframe is the onboarding tab of
 * the vehicles screen; the nav entry is the prefix, so a nested screen a sibling
 * component adds later resolves to it without a change here" — and the sketch
 * draws one screen whose tab strip selects between adding one vehicle and
 * importing a file. Two routes rendering that one screen would give the nav
 * highlight two places to be right and the roster two URLs to be bookmarked at.
 *
 * `permanentRedirect` rather than `redirect`: the address is not going to start
 * meaning something else, and a 308 lets a browser stop asking.
 *
 * `proxy.ts` has already gated the path — `resolveScreenRoute` matches the
 * longest entry and `/vehicles` covers everything under it, so a caller whose
 * seat does not carry the vehicles screen, or whose organisation is still in
 * verification, never reaches this file.
 */
export default function VehicleOnboardingRedirect(): never {
  permanentRedirect('/vehicles');
}
