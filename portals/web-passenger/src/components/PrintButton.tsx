'use client';

import { Button } from '@mageride/ui';

/**
 * SCR-WT-005's **Download receipt**.
 *
 * ## It prints, because there is no PDF to download
 *
 * `public-bff.yaml` offers `GET /public/track/{token}/receipt` as JSON, HTML **and**
 * `application/pdf`. The implemented route answers JSON only — `ReceiptAsync`
 * returns `Ok<ReceiptResponse>`, there is no content negotiation in the handler and
 * nothing in that assembly renders a document. A button that fetched a PDF would
 * download a JSON body with the wrong extension on it.
 *
 * So the page prints itself, which is the arrangement the Fleet Portal already
 * reached for SCR-FP-009 ("the CSV is written here; the PDF is the browser's"). The
 * printed sheet is the receipt: `print-hidden` takes the bar, the map and the
 * controls off, and every figure on the paper is a figure public-bff returned. The
 * alternative — composing a second receipt document in this repo — would be a
 * second statement about somebody's delivery, and the two could disagree.
 *
 * The gap is recorded in the C117 handoff. If the PDF lands on public-bff, this
 * becomes a link to it and the print rules stay as a fallback.
 *
 * Rendered as a client island of one control: nothing else on SCR-WT-005 needs
 * JavaScript, and a receipt that did not exist until a bundle loaded would be worse
 * than one that cannot be printed.
 */
export function PrintButton({ label }: { label: string }) {
  return (
    <Button
      variant="secondary"
      onClick={() => window.print()}
      className="print-hidden w-full"
    >
      <span aria-hidden="true">⬇</span>
      {label}
    </Button>
  );
}
