import { Router } from 'express';
import { param, validationResult } from 'express-validator';
import mongoose from 'mongoose';
import { User } from '../models/User.js';
import { UserBid } from '../models/UserBid.js';
import { Interview } from '../models/Interview.js';
import { GroupLink } from '../models/GroupLink.js';
import { Achievement, ACHIEVEMENT_KINDS } from '../models/Achievement.js';
import { Group } from '../models/Group.js';
import { requireAuth } from '../middleware/auth.js';
import { assertGroupMember } from '../services/membership.js';
import { mergeOverviewWeights } from '../constants/overviewScoreWeights.js';

const r = Router();
r.use(requireAuth);

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
  return { start: new Date(d.getTime() - 7 * 86400000), end: new Date(d) };
}

function utcMonthBounds(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = d.getUTCMonth();
  return {
    start: new Date(Date.UTC(y, m, 1, 0, 0, 0, 0)),
    end: new Date(Date.UTC(y, m + 1, 1, 0, 0, 0, 0)),
  };
}

function dayKey(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, '0');
  const day = String(d.getUTCDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}
function monthKey(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, '0');
  return `${y}-${m}`;
}

/** Today's badges/progress for the caller in this group. Always returns target/value pairs so the widget can render even with no achievements yet. */
r.get(
  '/groups/:groupId/achievements/me',
  param('groupId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const gid = new mongoose.Types.ObjectId(req.params.groupId);
    const uid = new mongoose.Types.ObjectId(req.user.id);
    const user = await User.findById(uid).select('goals').lean();
    const goals = {
      bidsPerDay: user?.goals?.bidsPerDay ?? 0,
      interviewsPerWeek: user?.goals?.interviewsPerWeek ?? 0,
      offersPerMonth: user?.goals?.offersPerMonth ?? 0,
    };

    const day = utcDayBounds();
    const week = rolling7DayBounds();
    const month = utcMonthBounds();

    const [bidsToday, interviewsThisWeek, offersThisMonth, activeBadges] = await Promise.all([
      UserBid.countDocuments({
        groupId: gid,
        userId: uid,
        /** Anything that's been submitted counts toward the daily bid goal. */
        status: { $ne: 'draft' },
        updatedAt: { $gte: day.start, $lt: day.end },
      }),
      Interview.countDocuments({
        groupId: gid,
        userId: uid,
        status: { $in: ['scheduled', 'completed', 'passed'] },
        createdAt: { $gte: week.start, $lt: week.end },
      }),
      UserBid.countDocuments({
        groupId: gid,
        userId: uid,
        status: { $in: ['offer', 'accepted'] },
        updatedAt: { $gte: month.start, $lt: month.end },
      }),
      Achievement.find({
        userId: uid,
        groupId: gid,
        $or: [
          { kind: 'daily_bids', periodKey: dayKey() },
          { kind: 'weekly_interviews', periodKey: dayKey() },
          { kind: 'monthly_offers', periodKey: monthKey() },
        ],
      })
        .select('kind periodKey achievedAt metricValue target')
        .lean(),
    ]);

    return res.json({
      goals,
      progress: {
        daily_bids: { value: bidsToday, target: goals.bidsPerDay },
        weekly_interviews: { value: interviewsThisWeek, target: goals.interviewsPerWeek },
        monthly_offers: { value: offersThisMonth, target: goals.offersPerMonth },
      },
      activeBadges: activeBadges.map((a) => ({
        kind: a.kind,
        periodKey: a.periodKey,
        achievedAt: a.achievedAt,
        metricValue: a.metricValue,
        target: a.target,
      })),
    });
  }
);

/**
 * Group leaderboard by overview score (current month window).
 * Score formula mirrors client `computeOverviewScore`; weights pulled from the group
 * (override) merged with defaults. Hides nickname/avatar when a member opted out via
 * `showOnLeaderboard: false` — they still see their own row, just as "anonymous" for others.
 */
r.get('/groups/:groupId/leaderboard', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const m = await assertGroupMember(req.user.id, req.params.groupId);
  if (!m.ok) return res.status(m.status).json({ error: m.error });

  const gid = new mongoose.Types.ObjectId(req.params.groupId);
  const group = await Group.findById(gid).select('memberIds overviewScoreWeights').lean();
  if (!group) return res.status(404).json({ error: 'Group not found' });

  const weights = mergeOverviewWeights(group.overviewScoreWeights);
  const month = utcMonthBounds();
  const memberIds = (group.memberIds || []).map((id) => new mongoose.Types.ObjectId(id));
  if (memberIds.length === 0) {
    return res.json({ rows: [], weights, window: { from: month.start, to: month.end } });
  }

  const [linksByUser, bidsCreatedByUser, bidsTouchedByUser, bidStatusByUser, interviewsByUser, users] =
    await Promise.all([
      GroupLink.aggregate([
        { $match: { groupId: gid, createdAt: { $gte: month.start, $lt: month.end } } },
        { $group: { _id: '$createdByUserId', n: { $sum: 1 } } },
      ]),
      UserBid.aggregate([
        {
          $match: {
            groupId: gid,
            userId: { $in: memberIds },
            createdAt: { $gte: month.start, $lt: month.end },
          },
        },
        { $group: { _id: '$userId', n: { $sum: 1 } } },
      ]),
      UserBid.aggregate([
        {
          $match: {
            groupId: gid,
            userId: { $in: memberIds },
            updatedAt: { $gte: month.start, $lt: month.end },
          },
        },
        { $group: { _id: '$userId', n: { $sum: 1 } } },
      ]),
      UserBid.aggregate([
        {
          $match: {
            groupId: gid,
            userId: { $in: memberIds },
            updatedAt: { $gte: month.start, $lt: month.end },
          },
        },
        { $group: { _id: { userId: '$userId', status: '$status' }, n: { $sum: 1 } } },
      ]),
      Interview.aggregate([
        {
          $match: {
            groupId: gid,
            userId: { $in: memberIds },
            createdAt: { $gte: month.start, $lt: month.end },
          },
        },
        {
          $group: {
            _id: {
              userId: '$userId',
              status: '$status',
              isAssessment: { $eq: ['$interviewType', 'ASSESSMENT'] },
            },
            n: { $sum: 1 },
          },
        },
      ]),
      User.find({ _id: { $in: memberIds } })
        .select('nickname avatarId showOnLeaderboard')
        .lean(),
    ]);

  const linkMap = new Map(linksByUser.map((x) => [String(x._id), x.n]));
  const bidsCreatedMap = new Map(bidsCreatedByUser.map((x) => [String(x._id), x.n]));
  const bidsTouchedMap = new Map(bidsTouchedByUser.map((x) => [String(x._id), x.n]));

  /** @type {Map<string, Record<string, number>>} */
  const byStatusMap = new Map();
  for (const row of bidStatusByUser) {
    const uid = String(row._id.userId);
    const s = row._id.status || 'unknown';
    if (!byStatusMap.has(uid)) byStatusMap.set(uid, {});
    byStatusMap.get(uid)[s] = (byStatusMap.get(uid)[s] ?? 0) + row.n;
  }

  /** @type {Map<string, { total: number, passed: number, failed: number, assessmentsTotal: number, assessmentsPassed: number, assessmentsFailed: number }>} */
  const ivMap = new Map();
  for (const row of interviewsByUser) {
    const uid = String(row._id.userId);
    const isA = !!row._id.isAssessment;
    const s = row._id.status || '';
    if (!ivMap.has(uid)) {
      ivMap.set(uid, {
        total: 0,
        passed: 0,
        failed: 0,
        assessmentsTotal: 0,
        assessmentsPassed: 0,
        assessmentsFailed: 0,
      });
    }
    const e = ivMap.get(uid);
    if (isA) e.assessmentsTotal += row.n;
    else e.total += row.n;
    if (s === 'passed') {
      if (isA) e.assessmentsPassed += row.n;
      else e.passed += row.n;
    } else if (s === 'failed') {
      if (isA) e.assessmentsFailed += row.n;
      else e.failed += row.n;
    }
  }

  function scoreForUser(uid) {
    const bs = byStatusMap.get(uid) ?? {};
    const iv = ivMap.get(uid) ?? {
      total: 0, passed: 0, failed: 0,
      assessmentsTotal: 0, assessmentsPassed: 0, assessmentsFailed: 0,
    };
    const interviewDecided = iv.passed + iv.failed;
    const assessmentDecided = iv.assessmentsPassed + iv.assessmentsFailed;
    const interviewPassRate = interviewDecided ? iv.passed / interviewDecided : 0;
    const assessmentPassRate = assessmentDecided ? iv.assessmentsPassed / assessmentDecided : 0;
    const w = weights;
    const g = (k) => bs[k] ?? 0;
    return (
      w.linksCreated * (linkMap.get(uid) ?? 0) +
      w.bidsCreated * (bidsCreatedMap.get(uid) ?? 0) +
      w.bidsTouched * (bidsTouchedMap.get(uid) ?? 0) +
      w.draft * g('draft') +
      w.applied * g('applied') +
      w.screening * g('screening') +
      w.interview * g('interview') +
      w.offer * g('offer') +
      w.rejected * g('rejected') +
      w.withdrawn * g('withdrawn') +
      w.accepted * g('accepted') +
      w.interviewsTotal * iv.total +
      w.interviewsPassed * iv.passed +
      w.interviewsFailed * iv.failed +
      w.interviewPassRate * interviewPassRate +
      w.assessmentsTotal * iv.assessmentsTotal +
      w.assessmentsPassed * iv.assessmentsPassed +
      w.assessmentsFailed * iv.assessmentsFailed +
      w.assessmentPassRate * assessmentPassRate
    );
  }

  const callerId = String(req.user.id);
  const rows = users
    .map((u) => {
      const uid = String(u._id);
      const score = scoreForUser(uid);
      const optedOut = u.showOnLeaderboard === false;
      const isCaller = uid === callerId;
      return {
        userId: uid,
        nickname: optedOut && !isCaller ? '' : u.nickname || '',
        avatarId: optedOut && !isCaller ? 'initial' : u.avatarId || 'initial',
        score: Math.round(score * 100) / 100,
        isCaller,
        anonymous: optedOut && !isCaller,
      };
    })
    .sort((a, b) => b.score - a.score)
    .map((row, i) => ({ ...row, rank: i + 1 }));

  return res.json({
    rows,
    weights,
    window: { from: month.start, to: month.end },
  });
});

export default r;
