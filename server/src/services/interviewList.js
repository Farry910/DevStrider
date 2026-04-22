import { escapeRegex } from '../utils/regex.js';

const SORT_FIELDS = new Set([
  'scheduledDate',
  'createdAt',
  'company',
  'role',
  'interviewType',
  'status',
  'recruiter',
  'meetingLink',
  'origin',
]);

export function parseInterviewSort(sortParam) {
  const [field, dirRaw] = String(sortParam || 'scheduledDate:desc').split(':');
  const f = SORT_FIELDS.has(field) ? field : 'scheduledDate';
  const dir = dirRaw === 'asc' ? 1 : -1;
  const primary = { [f]: dir };
  if (f !== 'createdAt') {
    return { ...primary, createdAt: -1 };
  }
  return primary;
}

function ts(v) {
  const s = v == null ? '' : String(v).trim();
  return s.length ? s : '';
}

/**
 * Interviews that fall in [from, to): scheduled on that day, or completed/passed/failed with updatedAt in range.
 * @param {Date} from
 * @param {Date} to
 */
export function buildInterviewTimeWindowClause(from, to) {
  return {
    $or: [
      { scheduledDate: { $ne: null, $gte: from, $lt: to } },
      {
        status: { $in: ['completed', 'passed', 'failed'] },
        updatedAt: { $gte: from, $lt: to },
      },
    ],
  };
}

export function buildInterviewMatch(groupId, userId, filters) {
  const base = { groupId, userId };
  if (!filters) return base;
  const and = [{ ...base }];
  if (ts(filters.company)) {
    and.push({ company: { $regex: escapeRegex(ts(filters.company)), $options: 'i' } });
  }
  if (ts(filters.role)) {
    and.push({ role: { $regex: escapeRegex(ts(filters.role)), $options: 'i' } });
  }
  if (ts(filters.recruiter)) {
    and.push({
      recruiter: { $regex: escapeRegex(ts(filters.recruiter)), $options: 'i' },
    });
  }
  if (ts(filters.interviewType)) {
    and.push({
      interviewType: { $regex: escapeRegex(ts(filters.interviewType)), $options: 'i' },
    });
  }
  if (ts(filters.status)) {
    and.push({ status: { $regex: escapeRegex(ts(filters.status)), $options: 'i' } });
  }
  if (ts(filters.origin)) {
    and.push({ origin: { $regex: escapeRegex(ts(filters.origin)), $options: 'i' } });
  }
  if (ts(filters.meetingLink)) {
    and.push({
      meetingLink: { $regex: escapeRegex(ts(filters.meetingLink)), $options: 'i' },
    });
  }
  if (ts(filters.userComment)) {
    and.push({
      userComment: { $regex: escapeRegex(ts(filters.userComment)), $options: 'i' },
    });
  }
  if (and.length === 1) return base;
  return { $and: and };
}

/** Merge base/wrapped match with a time window (AND). */
export function mergeInterviewMatchWithWindow(match, from, to) {
  const t0 = from instanceof Date ? from : new Date(from);
  const t1 = to instanceof Date ? to : new Date(to);
  const windowClause = buildInterviewTimeWindowClause(t0, t1);
  if (match.$and) {
    return { $and: [...match.$and, windowClause] };
  }
  return { $and: [match, windowClause] };
}
