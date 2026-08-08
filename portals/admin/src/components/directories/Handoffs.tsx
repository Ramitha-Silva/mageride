import Link from 'next/link';

/**
 * Where a directory record sends an operator who needs to *do* something.
 *
 * **Nothing on these three screens writes anything, and this is why that is
 * liveable.** BR-28.8: "All are read-only — refunds route to Finance and wallet
 * reversals stay Finance-only." So the wireframe's buttons — "Raise / link ticket",
 * "View documents", the reversal Finance may post "from here" — are drawn as links
 * to the screen that owns the action, prefilled with the subject, rather than as
 * controls this screen cannot honour. The address bar names who is about to be
 * suspended or credited before anything is pressed, which is the same shape
 * SCR-AP-004's suspend card is aimed by.
 *
 * **Each link appears only when the caller's own menu carries the screen it points
 * at** (`AlertsCard`'s rule, AL-06). A Support CSR gets the ticket queue and not the
 * reversal form; a Finance Officer gets the reversal form and not the verification
 * queues. A link the proxy would answer 403 on is worse than no link, because it
 * reads as a permission the operator has and a system that is broken.
 *
 * When the caller holds none of them the whole block is absent rather than an empty
 * heading: there is nothing to hand off to, and saying so twice helps nobody.
 */

export interface HandoffView {
  readonly key: string;
  readonly href: string;
  readonly label: string;
  readonly hint?: string;
}

export function Handoffs({
  heading,
  items,
}: {
  readonly heading: string;
  readonly items: readonly HandoffView[];
}) {
  if (items.length === 0) return null;

  return (
    <div className="flex flex-col gap-xs">
      <h3 className="text-label text-on-surface-variant">{heading}</h3>
      <ul className="flex flex-col gap-xxs">
        {items.map((item) => (
          <li key={item.key}>
            <Link
              href={item.href}
              className="inline-flex h-10 items-center rounded-sm border border-outline px-sm text-body-sm text-on-surface hover:bg-surface-variant"
            >
              {item.label}
              <span aria-hidden="true">{' ›'}</span>
            </Link>
            {item.hint ? (
              <p className="text-caption text-on-surface-variant">{item.hint}</p>
            ) : null}
          </li>
        ))}
      </ul>
    </div>
  );
}
