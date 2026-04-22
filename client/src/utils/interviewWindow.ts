import dayjs from 'dayjs';
import isoWeek from 'dayjs/plugin/isoWeek';

dayjs.extend(isoWeek);

export type InterviewRangeMode = 'week' | 'month';

/** Value for `<input type="week" />` (ISO week-year + week), e.g. `2026-W15`. */
export function defaultIsoWeekFieldValue(d = new Date()): string {
  const x = dayjs(d);
  const iy = x.isoWeekYear();
  const iw = x.isoWeek();
  return `${iy}-W${String(iw).padStart(2, '0')}`;
}

/** Value for `<input type="month" />`, e.g. `2026-04`. */
export function defaultMonthFieldValue(d = new Date()): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}

/**
 * Monday 00:00 local of ISO week `week` (1–53) in ISO week-year `weekYear`.
 * Based on Jan 4 anchor (always in week 1 of its ISO week-year).
 */
function isoWeekToLocalHalfOpenBounds(weekYear: number, week: number): { from: string; to: string } {
  const simple = new Date(weekYear, 0, 4);
  const dow = simple.getDay() || 7;
  const mondayW1 = new Date(simple);
  mondayW1.setDate(simple.getDate() - dow + 1);
  mondayW1.setHours(0, 0, 0, 0);
  const from = new Date(mondayW1);
  from.setDate(mondayW1.getDate() + (week - 1) * 7);
  from.setHours(0, 0, 0, 0);
  const to = new Date(from);
  to.setDate(from.getDate() + 7);
  to.setHours(0, 0, 0, 0);
  return { from: from.toISOString(), to: to.toISOString() };
}

/**
 * Half-open [from, to) in ISO, local midnights: that ISO week (Mon–Sun).
 */
export function interviewBoundsFromIsoWeekField(value: string): { from: string; to: string } {
  const m = /^(\d{4})-W(\d{1,2})$/.exec(value.trim());
  if (!m) {
    return interviewBoundsFromIsoWeekField(defaultIsoWeekFieldValue());
  }
  const weekYear = Number(m[1]);
  const week = Number(m[2]);
  if (!weekYear || week < 1 || week > 53) {
    return interviewBoundsFromIsoWeekField(defaultIsoWeekFieldValue());
  }
  return isoWeekToLocalHalfOpenBounds(weekYear, week);
}

/** Half-open [from, to) for that calendar month in local time. */
export function interviewBoundsFromMonthField(value: string): { from: string; to: string } {
  const m = /^(\d{4})-(\d{2})$/.exec(value.trim());
  if (!m) {
    return interviewBoundsFromMonthField(defaultMonthFieldValue());
  }
  const y = Number(m[1]);
  const mo = Number(m[2]);
  if (!y || mo < 1 || mo > 12) {
    return interviewBoundsFromMonthField(defaultMonthFieldValue());
  }
  const from = new Date(y, mo - 1, 1, 0, 0, 0, 0);
  const to = new Date(y, mo, 1, 0, 0, 0, 0);
  return { from: from.toISOString(), to: to.toISOString() };
}

/** Short label for the active range (inclusive end date for display). */
export function formatInterviewRangeCaption(
  mode: InterviewRangeMode,
  weekField: string,
  monthField: string
): string {
  if (mode === 'week') {
    const { from, to } = interviewBoundsFromIsoWeekField(weekField);
    const a = dayjs(from);
    const b = dayjs(new Date(new Date(to).getTime() - 86400000));
    return `${a.format('MMM D')} – ${b.format('MMM D, YYYY')}`;
  }
  const { from, to } = interviewBoundsFromMonthField(monthField);
  const a = dayjs(from);
  const b = dayjs(new Date(new Date(to).getTime() - 86400000));
  return `${a.format('MMMM YYYY')} (${a.format('MMM D')} – ${b.format('MMM D')})`;
}
