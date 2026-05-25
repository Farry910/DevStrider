import { User } from '../models/User.js';
import { UserBid } from '../models/UserBid.js';
import { Interview } from '../models/Interview.js';
import { Achievement } from '../models/Achievement.js';
import { Notification } from '../models/Notification.js';
import { emitNotificationToUser } from '../socket/hexGameSocket.js';

/** UTC YYYY-MM-DD. */
function periodKeyDay(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, '0');
  const day = String(d.getUTCDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

/** Rolling 7-day window key — uses the current UTC day's YMD as the period key (one badge per day max). */
function periodKeyWeek(d = new Date()) {
  return periodKeyDay(d);
}

/** UTC YYYY-MM. */
function periodKeyMonth(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, '0');
  return `${y}-${m}`;
}

function utcDayBounds(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = d.getUTCMonth();
  const day = d.getUTCDate();
  return {
    start: new Date(Date.UTC(y, m, day, 0, 0, 0, 0)),
    end: new Date(Date.UTC(y, m, day + 1, 0, 0, 0, 0)),
  };
}

function rolling7DayBounds(d = new Date()) {
  const end = new Date(d);
  const start = new Date(d.getTime() - 7 * 86400000);
  return { start, end };
}

function utcMonthBounds(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = d.getUTCMonth();
  return {
    start: new Date(Date.UTC(y, m, 1, 0, 0, 0, 0)),
    end: new Date(Date.UTC(y, m + 1, 1, 0, 0, 0, 0)),
  };
}

const KIND_HANDLERS = {
  async daily_bids({ userId, groupId, target }) {
    const { start, end } = utcDayBounds();
    /**
     * Any non-draft bid counts as an applied bid for the daily goal — once submitted, the bid
     * stays on the books even if its status later moves to interview/offer/etc. Only draft
     * (uncommitted) rows don't count.
     */
    const count = await UserBid.countDocuments({
      groupId,
      userId,
      status: { $ne: 'draft' },
      updatedAt: { $gte: start, $lt: end },
    });
    return { count, periodKey: periodKeyDay() };
  },
  async weekly_interviews({ userId, groupId, target }) {
    const { start, end } = rolling7DayBounds();
    const count = await Interview.countDocuments({
      groupId,
      userId,
      status: { $in: ['scheduled', 'completed', 'passed'] },
      createdAt: { $gte: start, $lt: end },
    });
    return { count, periodKey: periodKeyWeek() };
  },
  async monthly_offers({ userId, groupId, target }) {
    const { start, end } = utcMonthBounds();
    const count = await UserBid.countDocuments({
      groupId,
      userId,
      status: { $in: ['offer', 'accepted'] },
      updatedAt: { $gte: start, $lt: end },
    });
    return { count, periodKey: periodKeyMonth() };
  },
};

const KIND_TO_GOAL_FIELD = {
  daily_bids: 'bidsPerDay',
  weekly_interviews: 'interviewsPerWeek',
  monthly_offers: 'offersPerMonth',
};

/**
 * Check whether the given user has hit their goal for `kind` in this UTC period. If so,
 * create an Achievement + Notification + socket event (idempotent on the unique index).
 * Safe to call from any write path; failures are logged and never thrown.
 */
export async function checkAndAwardAchievement({ userId, groupId, kind }) {
  try {
    if (!userId || !groupId || !KIND_HANDLERS[kind]) return null;
    const user = await User.findById(userId).select('goals nickname').lean();
    if (!user) return null;
    const goalField = KIND_TO_GOAL_FIELD[kind];
    const target = Number(user.goals?.[goalField] ?? 0);
    if (!Number.isFinite(target) || target <= 0) return null;

    const { count, periodKey } = await KIND_HANDLERS[kind]({ userId, groupId, target });
    if (count < target) return null;

    let achievement;
    try {
      achievement = await Achievement.create({
        userId,
        groupId,
        kind,
        periodKey,
        metricValue: count,
        target,
      });
    } catch (e) {
      // Duplicate-key = already awarded for this period; treat as success-no-op.
      if (e?.code === 11000) return null;
      throw e;
    }

    const notif = await Notification.create({
      userId,
      kind: 'achievement',
      payload: {
        groupId: String(groupId),
        achievementId: String(achievement._id),
        achievementKind: kind,
        periodKey,
        target,
        metricValue: count,
      },
    });

    emitNotificationToUser(String(userId), {
      id: String(notif._id),
      kind: 'achievement',
      payload: notif.payload,
      createdAt: notif.createdAt,
    });

    return { achievement, notification: notif };
  } catch (e) {
    console.error('checkAndAwardAchievement failed', { userId, groupId, kind, err: e?.message });
    return null;
  }
}

/** Fire-and-forget convenience: re-evaluate all kinds for a user-group after a write. */
export function awardAchievementsAsync({ userId, groupId, kinds }) {
  const list = Array.isArray(kinds) && kinds.length > 0 ? kinds : Object.keys(KIND_HANDLERS);
  for (const k of list) {
    void checkAndAwardAchievement({ userId, groupId, kind: k });
  }
}
