import {
  driverQuery,
  passengerQuery,
  vehicleQuery,
  withQuery,
  type DriverSelection,
  type PassengerSelection,
  type VehicleSelection,
} from '@/api/directories';
import type { AdminMenuGroup } from '@/api/types';
import { permittedItems } from '@/server/access';

/**
 * The URLs SCR-AP-010…015 navigate between.
 *
 * They are in one module because the operator's **search has to survive the round
 * trip**: find a driver by NIC, open them, follow a vehicle chip, come back, and
 * the NIC is still in the box. Every link a detail draws therefore carries the
 * results query onward, and one place that builds it is what stops three of them
 * agreeing while a fourth quietly drops the plate. C106's `verification/links.ts`
 * exists for the same reason.
 *
 * ## Two kinds of path, and only one of them is written here
 *
 * A link **within** this screen group is built from the group's own route, because
 * `/passengers/{id}` is this component's file layout and nothing else decides it. A
 * link **out** of it — to the reversal form, the suspend card, the verification
 * subject, the ticket queue — is built from the path the item carried on
 * `GET /v1/admin/session`, never from `src/server/routes.ts`. The server's own path
 * is the one the server's own gate agrees with, and an item the caller does not
 * hold produces no link at all.
 */

export const PASSENGERS_ROUTE = '/passengers';
export const DRIVERS_ROUTE = '/drivers';
export const VEHICLES_ROUTE = '/vehicles';

/** Back to the results the operator came from, with their criteria intact. */
export function passengersHref(selection: PassengerSelection): string {
  return withQuery(PASSENGERS_ROUTE, passengerQuery(selection));
}

export function driversHref(selection: DriverSelection): string {
  return withQuery(DRIVERS_ROUTE, driverQuery(selection));
}

export function vehiclesHref(selection: VehicleSelection): string {
  return withQuery(VEHICLES_ROUTE, vehicleQuery(selection));
}

/** One row's detail, carrying the search that found it. */
export function passengerHref(selection: PassengerSelection, passengerId: string): string {
  return withQuery(`${PASSENGERS_ROUTE}/${passengerId}`, passengerQuery(selection));
}

export function driverHref(selection: DriverSelection, driverId: string): string {
  return withQuery(`${DRIVERS_ROUTE}/${driverId}`, driverQuery(selection));
}

export function vehicleHref(selection: VehicleSelection, vehicleId: string): string {
  return withQuery(`${VEHICLES_ROUTE}/${vehicleId}`, vehicleQuery(selection));
}

/**
 * A tab on a detail, keeping the search that got the operator here.
 *
 * The tab is appended to the record's own query rather than replacing it, so
 * "Wallet ledger" and "back to the drivers I was looking at" are one URL.
 */
export function tabHref(
  detailHref: string,
  tab: string,
  firstTab: string,
): string {
  // The first tab is the default and carries no parameter: a URL that says
  // `?tab=trips` and a URL that says nothing are the same screen, and only one of
  // them should be what a copy-paste produces.
  if (tab === firstTab) return detailHref;

  const separator = detailHref.includes('?') ? '&' : '?';
  return `${detailHref}${separator}tab=${encodeURIComponent(tab)}`;
}

/** The portal's relay of an audited document rendition, on the vehicle directory's own gate. */
export function vehicleMediaHref(docId: string, variant: 'thumb' | 'full'): string {
  return `${VEHICLES_ROUTE}/media/${docId}?variant=${variant}`;
}

/** SCR-AP-003b, opened from a vehicle record rather than from a verification queue. */
export function vehicleDocHref(
  selection: VehicleSelection,
  vehicleId: string,
  docId: string,
): string {
  return withQuery(`${VEHICLES_ROUTE}/${vehicleId}/doc/${docId}`, vehicleQuery(selection));
}

/**
 * The path the **caller's own menu** gives a screen, or `undefined` where they do
 * not hold it. The one way a link out of this screen group is drawn.
 */
export function menuPath(
  menu: readonly AdminMenuGroup[],
  navKey: string,
): string | undefined {
  return permittedItems(menu).find(({ item }) => item.key === navKey)?.item.path;
}
