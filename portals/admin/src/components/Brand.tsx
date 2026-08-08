/**
 * The wordmark, as `web_admin.html` draws it: a rounded `primary` tile with an M
 * in it, and the product name beside it.
 *
 * `label` is `admin.appName`, translated by the caller — "MageRide පරිපාලනය" in
 * Sinhala, "MageRide நிர்வாகம்" in Tamil. The tile takes its letter from that
 * string rather than carrying one of its own, so the mark can never disagree with
 * the name beside it.
 */
export function Brand({ label, size = 'md' }: { label: string; size?: 'md' | 'lg' }) {
  const large = size === 'lg';

  return (
    <span className="flex items-center gap-xs">
      <span
        aria-hidden="true"
        className={`grid shrink-0 place-items-center rounded-sm bg-primary font-display font-bold text-on-primary ${
          large ? 'size-11 text-headline' : 'size-7 text-subtitle'
        }`}
      >
        {label.charAt(0)}
      </span>
      <span className={`font-display font-bold ${large ? 'text-title' : 'text-subtitle'}`}>
        {label}
      </span>
    </span>
  );
}
