/** Weights for linear score model (negative weights penalize). */

export type OverviewScoreWeights = {
  linksCreated: number;
  bidsCreated: number;
  bidsTouched: number;
  draft: number;
  applied: number;
  screening: number;
  interview: number;
  offer: number;
  rejected: number;
  withdrawn: number;
  accepted: number;
  interviewsTotal: number;
  interviewsPassed: number;
  interviewsFailed: number;
  /** Multiplies interview pass rate in [0, 1] (e.g. 40 → up to 40 pts at 100% pass). */
  interviewPassRate: number;
  assessmentsTotal: number;
  assessmentsPassed: number;
  assessmentsFailed: number;
  /** Pass rate for ASSESSMENT rows only; typically keep weight lower than interviewPassRate. */
  assessmentPassRate: number;
};

export const DEFAULT_OVERVIEW_WEIGHTS: OverviewScoreWeights = {
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
  assessmentsTotal: 1,
  assessmentsPassed: 2,
  assessmentsFailed: -1.25,
  assessmentPassRate: 10,
};

/** Merge API/stored partial weights with defaults (same rules as server). */
export function mergeOverviewWeightsPartial(
  partial: Partial<OverviewScoreWeights> | null | undefined
): OverviewScoreWeights {
  const out = { ...DEFAULT_OVERVIEW_WEIGHTS };
  if (!partial || typeof partial !== 'object') return out;
  for (const k of Object.keys(DEFAULT_OVERVIEW_WEIGHTS) as (keyof OverviewScoreWeights)[]) {
    const v = partial[k];
    if (typeof v === 'number' && Number.isFinite(v)) out[k] = v;
  }
  return out;
}

export type OverviewScoreRowInput = {
  linksCreated: number;
  bidsCreatedInRange: number;
  bidsTouchedInRange: number;
  byStatus: Record<string, number>;
  interviewsInRange: number;
  interviewsPassed: number;
  interviewsFailed: number;
  interviewPassRate: number | null;
  assessmentsInRange: number;
  assessmentsPassed: number;
  assessmentsFailed: number;
  assessmentPassRate: number | null;
};

export function computeOverviewScore(row: OverviewScoreRowInput, w: OverviewScoreWeights): number {
  const bs = row.byStatus;
  const g = (k: string) => bs[k] ?? 0;
  return (
    w.linksCreated * row.linksCreated +
    w.bidsCreated * row.bidsCreatedInRange +
    w.bidsTouched * row.bidsTouchedInRange +
    w.draft * g('draft') +
    w.applied * g('applied') +
    w.screening * g('screening') +
    w.interview * g('interview') +
    w.offer * g('offer') +
    w.rejected * g('rejected') +
    w.withdrawn * g('withdrawn') +
    w.accepted * g('accepted') +
    w.interviewsTotal * row.interviewsInRange +
    w.interviewsPassed * row.interviewsPassed +
    w.interviewsFailed * row.interviewsFailed +
    w.interviewPassRate * (row.interviewPassRate ?? 0) +
    w.assessmentsTotal * row.assessmentsInRange +
    w.assessmentsPassed * row.assessmentsPassed +
    w.assessmentsFailed * row.assessmentsFailed +
    w.assessmentPassRate * (row.assessmentPassRate ?? 0)
  );
}

export const OVERVIEW_WEIGHT_FIELD_META: { key: keyof OverviewScoreWeights; label: string }[] = [
  { key: 'linksCreated', label: 'Links created' },
  { key: 'bidsCreated', label: 'New bids' },
  { key: 'bidsTouched', label: 'Bids touched' },
  { key: 'draft', label: 'Status: draft' },
  { key: 'applied', label: 'Status: applied' },
  { key: 'screening', label: 'Status: screening' },
  { key: 'interview', label: 'Status: interview' },
  { key: 'offer', label: 'Status: offer' },
  { key: 'rejected', label: 'Status: rejected' },
  { key: 'withdrawn', label: 'Status: withdrawn' },
  { key: 'accepted', label: 'Status: accepted' },
  { key: 'interviewsTotal', label: 'Interviews (total)' },
  { key: 'interviewsPassed', label: 'Interviews passed' },
  { key: 'interviewsFailed', label: 'Interviews failed' },
  { key: 'interviewPassRate', label: 'Interview pass rate (× rate 0–1)' },
  { key: 'assessmentsTotal', label: 'Assessments (total)' },
  { key: 'assessmentsPassed', label: 'Assessments passed' },
  { key: 'assessmentsFailed', label: 'Assessments failed' },
  { key: 'assessmentPassRate', label: 'Assessment pass rate (× rate 0–1)' },
];
