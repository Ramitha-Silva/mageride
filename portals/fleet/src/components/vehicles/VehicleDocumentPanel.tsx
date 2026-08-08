import Link from 'next/link';

import {
  VEHICLE_DOCUMENT_ACCEPT,
  VEHICLE_DOCUMENT_MAX_BYTES,
  VEHICLE_DOCUMENT_SLOTS,
  slotIsRequiredFor,
  type FleetVehicle,
  type VehicleDocumentSlot,
} from '@/api/vehicles';
import type { FleetTranslator, Locale } from '@/i18n';

import { DocumentSlotCard } from './DocumentSlotCard';
import {
  approvalGateSentence,
  extractedFieldView,
  slotExpiry,
  slotName,
  slotRequirementLabel,
  slotStatusLabel,
} from './vehicle-model';

/**
 * **AL-50's four named slots for one vehicle** (US-27.3, SCR-FP-004) — and the
 * sentence that says what the vehicle is waiting on.
 *
 * ## Four cards, mounted from a literal list
 *
 * The component fence is "no generic dropzone". The four are mounted from
 * {@link VEHICLE_DOCUMENT_SLOTS} — a list in `@/api/vehicles` with the wire kind
 * and the caption for each — rather than from whatever the server answered with,
 * so the screen draws exactly the four AL-50 names and cannot grow a fifth by
 * receiving one. What the *server* decides is each slot's chip and whether it is
 * required; what this file decides is that there are four boxes and what they are
 * called.
 *
 * ## The permit box is drawn on a Mode B vehicle, and marked optional
 *
 * fleet-svc renders the whole slot set whatever the mode — "a Mode B vehicle's
 * permit box is an empty optional one, not an absent one" — and refuses an upload
 * into it (`validation-failed`, "a route permit belongs to a Mode A
 * passenger-transport vehicle"). So the card is present, captioned optional and
 * disabled: an operator who has both kinds of vehicle sees the same four boxes on
 * both and learns which one differs.
 *
 * ## The approval gate is a sentence, not a chip
 *
 * "A vehicle cannot reach Approved while a required document is Missing or
 * Pending" is US-27.3's rule and `VehicleApprovalService`'s refusal. The panel
 * names the slots that are holding it, because "waiting on the insurance
 * certificate (Pending)" is something an operator can act on and an amber chip is
 * not.
 */

export function VehicleDocumentPanel({
  vehicle,
  slots,
  canUpload,
  locale,
  t,
}: {
  vehicle: FleetVehicle;
  slots: readonly VehicleDocumentSlot[];
  canUpload: boolean;
  locale: Locale;
  t: FleetTranslator;
}) {
  const gate = approvalGateSentence(slots, t);

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-subtitle font-semibold">
          {t('fleet.vehicles.docs.forVehicle', { plate: vehicle.registrationNumber })}
        </h2>
        <Link href="/vehicles" className="text-body-sm text-primary underline underline-offset-2">
          {t('fleet.vehicles.docs.backToRoster')}
        </Link>
      </div>

      <p className="text-caption text-on-surface-variant">{t('fleet.vehicles.docs.extraction')}</p>

      <section
        className={`flex flex-col gap-xxs rounded-md border px-sm py-xs ${
          gate.ready ? 'border-success/40 bg-success/10' : 'border-warning/40 bg-warning/10'
        }`}
      >
        <h3 className="text-body-sm font-semibold">{t('fleet.vehicles.docs.approvalGate')}</h3>
        <p className="text-body-sm">{gate.text}</p>
      </section>

      <div className="grid gap-sm md:grid-cols-2">
        {VEHICLE_DOCUMENT_SLOTS.map((spec) => {
          // The server's slot when it sent one; a Missing slot with AL-50's own
          // requirement rule when it did not, so a vehicle whose documents have
          // never been read still draws four boxes rather than none.
          const slot: VehicleDocumentSlot = slots.find((entry) => entry.kind === spec.kind) ?? {
            kind: spec.kind,
            status: 'missing',
            required: slotIsRequiredFor(spec.kind, vehicle.mode),
          };

          const status = slotStatusLabel(slot.status, t);
          const permitOnModeB = spec.kind === 'permit' && vehicle.mode !== 'A';

          return (
            <DocumentSlotCard
              key={spec.kind}
              vehicleId={vehicle.vehicleId}
              kind={spec.uploadKind}
              accept={VEHICLE_DOCUMENT_ACCEPT}
              present={Boolean(slot.docId)}
              // A route permit on a Mode B vehicle has nowhere to go: fleet-svc
              // refuses the upload, so the box is drawn and not armed.
              disabled={!canUpload || permitOnModeB}
              fields={(slot.fields ?? []).map((field) => extractedFieldView(field, t))}
              labels={{
                title: slotName(spec.kind, t),
                hint: t(spec.hintKey),
                status: status.label,
                statusTone: status.tone,
                requirement: slotRequirementLabel(slot, t),
                prompt: t('fleet.vehicles.doc.upload'),
                accept: t('fleet.vehicles.doc.accept', {
                  megabytes: VEHICLE_DOCUMENT_MAX_BYTES / (1024 * 1024),
                }),
                expiry: t('fleet.vehicles.field.expiry'),
                expiryHint: t('fleet.vehicles.field.expiryHint'),
                expires: slotExpiry(slot, locale, t),
                uploading: t('fleet.vehicles.slot.uploading'),
                replace: t('fleet.vehicles.slot.replace'),
                extracted: t('fleet.vehicles.slot.extracted'),
                rejectedType: t('fleet.error.fileNotAccepted'),
                rejectedSize: t('fleet.error.fileTooLarge', {
                  megabytes: VEHICLE_DOCUMENT_MAX_BYTES / (1024 * 1024),
                }),
              }}
            />
          );
        })}
      </div>
    </section>
  );
}
