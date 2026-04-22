/** Defaults mirror client `DEFAULT_OVERVIEW_WEIGHTS`. */

export const DEFAULT_OVERVIEW_WEIGHTS = {
  linksCreated: 2,
  bidsCreated: 3,
  bidsTouched: 1,
  draft: 0.5,
  applied: 2,
  screening: 3,
  interview: 5,
  offer: 10,
  rejected: -2,
  withdrawn: -1,
  accepted: 15,
  interviewsTotal: 4,
  interviewsPassed: 8,
  interviewsFailed: -5,
  interviewPassRate: 40,
  /** Take-home / async assessments (interview type ASSESSMENT) — lower default weight than live interviews. */
  assessmentsTotal: 1,
  assessmentsPassed: 2,
  assessmentsFailed: -1.25,
  assessmentPassRate: 10,
};

export const OVERVIEW_WEIGHT_KEYS = Object.keys(DEFAULT_OVERVIEW_WEIGHTS);

/**
 * @param {unknown} partial
 * @returns {typeof DEFAULT_OVERVIEW_WEIGHTS}
 */
export function mergeOverviewWeights(partial) {
  const out = { ...DEFAULT_OVERVIEW_WEIGHTS };
  if (!partial || typeof partial !== 'object') return out;
  for (const k of OVERVIEW_WEIGHT_KEYS) {
    const v = /** @type {Record<string, unknown>} */ (partial)[k];
    if (typeof v === 'number' && Number.isFinite(v)) out[k] = v;
  }
  return out;
}
