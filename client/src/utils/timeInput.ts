/**
 * Normalize stored time strings to `HH:mm` for `<input type="time" />` (24h).
 */
export function toHtmlTimeInputValue(raw: string | null | undefined): string {
  const s = String(raw ?? '').trim();
  if (!s) return '';
  const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?/.exec(s);
  if (m) {
    let hh = parseInt(m[1], 10);
    let mm = parseInt(m[2], 10);
    if (Number.isNaN(hh) || Number.isNaN(mm)) return '';
    hh = Math.min(23, Math.max(0, hh));
    mm = Math.min(59, Math.max(0, mm));
    return `${String(hh).padStart(2, '0')}:${String(mm).padStart(2, '0')}`;
  }
  return '';
}
