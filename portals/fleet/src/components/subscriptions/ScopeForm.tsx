import { Button, Field, Select } from '@mageride/ui';

import type { VehicleChoice } from './subscription-model';

/**
 * **The vehicle (and, on SCR-FP-012, the subscriber) these screens are scoped
 * to** — the wireframe's topbar "Vehicle: VN-8810 · Office Van (Paid) ▾".
 *
 * AL-23 makes this control load-bearing rather than convenient: there is no
 * fleet-wide request queue and no fleet-wide roster on any contract, so a vehicle
 * is the address at which an answer exists at all. Choosing one is choosing what
 * the page is about.
 *
 * A plain `method="get"` form with no `action`, so the choice lands in this
 * screen's own query string and the server re-renders from it — the same shape
 * SCR-FP-009's date filter takes, and for the same three reasons: the selection is
 * a URL that can be bookmarked and pasted, the export link can be handed the same
 * two values, and there is no client-side state that could disagree with the
 * figures beside it.
 *
 * A server component.
 */

export interface SubscriberChoice {
  readonly subscriberId: string;
  readonly label: string;
}

export interface ScopeFormLabels {
  readonly legend: string;
  readonly vehicle: string;
  readonly subscriber: string;
  readonly apply: string;
  /** Shown instead of the picker when the organisation runs no Mode B vehicle. */
  readonly noVehicles: string;
}

export function ScopeForm({
  vehicles,
  selectedVehicleId,
  subscribers,
  selectedSubscriberId,
  labels,
}: {
  vehicles: readonly VehicleChoice[];
  selectedVehicleId: string | null;
  /** `null` on SCR-FP-011, which is scoped by vehicle alone. */
  subscribers: readonly SubscriberChoice[] | null;
  selectedSubscriberId: string | null;
  labels: ScopeFormLabels;
}) {
  if (vehicles.length === 0) {
    return (
      <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
        {labels.noVehicles}
      </p>
    );
  }

  return (
    <form
      method="get"
      aria-label={labels.legend}
      className="flex flex-col gap-sm md:flex-row md:items-end"
    >
      <Field label={labels.vehicle} className="md:w-80">
        <Select name="vehicle" defaultValue={selectedVehicleId ?? vehicles[0]?.vehicleId}>
          {vehicles.map((vehicle) => (
            <option key={vehicle.vehicleId} value={vehicle.vehicleId}>
              {vehicle.label}
            </option>
          ))}
        </Select>
      </Field>

      {subscribers ? (
        <Field label={labels.subscriber} className="md:w-80">
          <Select name="subscriber" defaultValue={selectedSubscriberId ?? ''}>
            {subscribers.map((subscriber) => (
              <option key={subscriber.subscriberId} value={subscriber.subscriberId}>
                {subscriber.label}
              </option>
            ))}
          </Select>
        </Field>
      ) : null}

      <Button type="submit" variant="secondary" size="compact" className="md:mb-xxs">
        {labels.apply}
      </Button>
    </form>
  );
}
