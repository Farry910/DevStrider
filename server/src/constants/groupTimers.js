/** Defaults merged with `Group.timers` from DB (owner can override). */

export const DEFAULT_GROUP_TIMERS = {
  /** Minutes after “useless” mark before auto-removal (owner can remove immediately). */
  junkRemovalGraceMinutes: 10,
  /** Only consider job links created in the last N days when flagging duplicate URL / company+role. */
  bidDuplicateLookbackDays: 365,
  /** Reserved for future timed features (0 = off). Owner-set. */
  possibleTimerMinutes: 0,
};

/**
 * @param {unknown} v
 * @returns {typeof DEFAULT_GROUP_TIMERS}
 */
function clampTimersFromDoc(v) {
  const base = { ...DEFAULT_GROUP_TIMERS };
  if (!v || typeof v !== 'object' || Array.isArray(v)) return base;
  const o = v;
  if (typeof o.junkRemovalGraceMinutes === 'number' && !Number.isNaN(o.junkRemovalGraceMinutes)) {
    base.junkRemovalGraceMinutes = Math.min(
      10080,
      Math.max(1, Math.round(o.junkRemovalGraceMinutes))
    );
  }
  if (typeof o.bidDuplicateLookbackDays === 'number' && !Number.isNaN(o.bidDuplicateLookbackDays)) {
    base.bidDuplicateLookbackDays = Math.min(
      3650,
      Math.max(1, Math.round(o.bidDuplicateLookbackDays))
    );
  }
  if (typeof o.possibleTimerMinutes === 'number' && !Number.isNaN(o.possibleTimerMinutes)) {
    base.possibleTimerMinutes = Math.min(10080, Math.max(0, Math.round(o.possibleTimerMinutes)));
  }
  return base;
}

/** Merge owner PATCH body with stored and defaults. */
export function mergeGroupTimers(stored, partial) {
  const current = clampTimersFromDoc(stored);
  if (!partial || typeof partial !== 'object' || Array.isArray(partial)) return current;
  const next = { ...current };
  if (Object.prototype.hasOwnProperty.call(partial, 'junkRemovalGraceMinutes')) {
    const n = Number(partial.junkRemovalGraceMinutes);
    if (!Number.isNaN(n)) {
      next.junkRemovalGraceMinutes = Math.min(10080, Math.max(1, Math.round(n)));
    }
  }
  if (Object.prototype.hasOwnProperty.call(partial, 'bidDuplicateLookbackDays')) {
    const n = Number(partial.bidDuplicateLookbackDays);
    if (!Number.isNaN(n)) {
      next.bidDuplicateLookbackDays = Math.min(3650, Math.max(1, Math.round(n)));
    }
  }
  if (Object.prototype.hasOwnProperty.call(partial, 'possibleTimerMinutes')) {
    const n = Number(partial.possibleTimerMinutes);
    if (!Number.isNaN(n)) {
      next.possibleTimerMinutes = Math.min(10080, Math.max(0, Math.round(n)));
    }
  }
  return next;
}

export function resolvedTimersFromGroupDoc(groupDoc) {
  return clampTimersFromDoc(groupDoc?.timers);
}
