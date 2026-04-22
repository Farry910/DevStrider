/** Local calendar day as `YYYY-MM-DD` (browser timezone). */
export function todayLocalYmd(): string {
  const n = new Date();
  const y = n.getFullYear();
  const m = String(n.getMonth() + 1).padStart(2, '0');
  const d = String(n.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/** Local `YYYY-MM-DD` for a UTC / ISO timestamp (browser timezone). */
export function localYmdFromInstant(isoOrMs: string | number | Date): string {
  const n = typeof isoOrMs === 'string' || typeof isoOrMs === 'number' ? new Date(isoOrMs) : isoOrMs;
  const y = n.getFullYear();
  const m = String(n.getMonth() + 1).padStart(2, '0');
  const d = String(n.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/** Inclusive start, exclusive end in ISO for the given local date string from `<input type="date" />`. */
export function localDayIsoRange(ymd: string): { from: string; to: string } {
  const [y, m, d] = ymd.split('-').map(Number);
  const from = new Date(y, m - 1, d, 0, 0, 0, 0);
  const to = new Date(y, m - 1, d + 1, 0, 0, 0, 0);
  return { from: from.toISOString(), to: to.toISOString() };
}
