import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

import type { VehicleChipView } from './model';

/**
 * SCR-AP-013's linked-vehicle chips — the wireframe's "Vehicles: **Sedan
 * ABC-1234** (Approved) · → SCR-AP-015".
 *
 * **Owned _or_ assigned, and the chip says which.** A Mode C driver owns their
 * vehicle; a fleet's driver owns nothing and drives what `registry.fleet_assignments`
 * gives them (AL-03). Both are "linked vehicles" (US-24.10) and an operator looking
 * at a suspension needs to know whether the plate belongs to the person in front of
 * them or to Lanka Transit.
 *
 * **A chip is a link only when the caller's menu carries the vehicle directory.**
 * `LinkedVehicle.link` is the *API* path and is deliberately unused; the portal
 * route comes from the item `GET /v1/admin/session` sent, so a chip the proxy would
 * refuse is never drawn as one. Without the item the chip is still drawn as text,
 * because "this driver also drives NB-4412" is a fact a Finance Officer
 * reconciling a daily fee needs whether or not they may open that vehicle's screen.
 */

export function LinkedVehicles({
  vehicles,
  labels,
}: {
  readonly vehicles: readonly VehicleChipView[];
  readonly labels: { readonly heading: string; readonly empty: string };
}) {
  return (
    <div className="flex flex-col gap-xs">
      <h3 className="text-label text-on-surface-variant">{labels.heading}</h3>

      {vehicles.length === 0 ? (
        <p className="text-body-sm text-on-surface-variant">{labels.empty}</p>
      ) : (
        <ul className="flex flex-col gap-xs">
          {vehicles.map((vehicle) => (
            <li
              key={vehicle.key}
              className="flex flex-col gap-xxs rounded-md border border-outline bg-surface p-xs"
            >
              <span className="flex flex-wrap items-center gap-xs">
                {vehicle.href ? (
                  <Link
                    href={vehicle.href}
                    className="text-body-sm font-semibold text-primary underline underline-offset-2"
                  >
                    {vehicle.regNo}
                  </Link>
                ) : (
                  <span className="text-body-sm font-semibold text-on-surface">{vehicle.regNo}</span>
                )}
                <StatusPill tone={vehicle.status.tone}>{vehicle.status.label}</StatusPill>
                {vehicle.suspended ? (
                  <StatusPill tone={vehicle.suspended.tone}>{vehicle.suspended.label}</StatusPill>
                ) : null}
              </span>
              <span className="text-caption text-on-surface-variant">
                {vehicle.detail}
                <span aria-hidden="true">{' · '}</span>
                {vehicle.ownership}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
