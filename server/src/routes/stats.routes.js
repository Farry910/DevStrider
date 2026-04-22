import { Router } from 'express';
import { param, query, validationResult } from 'express-validator';
import mongoose from 'mongoose';
import { UserBid } from '../models/UserBid.js';
import { Interview } from '../models/Interview.js';
import { User } from '../models/User.js';
import { Group } from '../models/Group.js';
import { GroupLink } from '../models/GroupLink.js';
import { requireAuth } from '../middleware/auth.js';
import { assertGroupMember } from '../services/membership.js';
import { mergeInterviewMatchWithWindow } from '../services/interviewList.js';

const r = Router();
r.use(requireAuth);

/** Half-open window [from, to) — same convention as interview list. */
function parseRange(q) {
  const now = new Date();
  let from;
  let to = now;
  if (q.from && q.to) {
    from = new Date(q.from);
    to = new Date(q.to);
  } else if (q.range === 'week') {
    from = new Date(now);
    from.setDate(from.getDate() - 7);
  } else if (q.range === 'month') {
    from = new Date(now);
    from.setMonth(from.getMonth() - 1);
  } else {
    from = new Date(now);
    from.setDate(from.getDate() - 30);
  }
  return { from, to };
}

function parseWindowStrict(q) {
  if (q.from == null || q.from === '' || q.to == null || q.to === '') return null;
  const from = new Date(q.from);
  const to = new Date(q.to);
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()) || to <= from) return null;
  return { from, to };
}

function memberObjectIdsFilter(q) {
  if (!q.userIds) return null;
  const ids = q.userIds.split(',').filter(Boolean);
  if (!ids.length) return null;
  return ids.map((id) => new mongoose.Types.ObjectId(id));
}

function bidMemberFilter(q) {
  const oidList = memberObjectIdsFilter(q);
  if (!oidList) return {};
  return { userId: { $in: oidList } };
}

function linkCreatorFilter(q) {
  const oidList = memberObjectIdsFilter(q);
  if (!oidList) return {};
  return { createdByUserId: { $in: oidList } };
}

/** Group bid status breakdown + success approximations (offer/accepted vs rejected). */
r.get(
  '/groups/:groupId/stats/bids',
  param('groupId').isMongoId(),
  query('range').optional().isIn(['week', 'month', 'custom']),
  query('from').optional().isISO8601(),
  query('to').optional().isISO8601(),
  query('userIds').optional().isString(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const strict = parseWindowStrict(req.query);
    const { from, to } = strict || parseRange(req.query);
    const groupId = new mongoose.Types.ObjectId(req.params.groupId);
    const memberFilter = bidMemberFilter(req.query);

    const pipeline = [
      {
        $match: {
          groupId,
          updatedAt: { $gte: from, $lt: to },
          ...memberFilter,
        },
      },
      {
        $group: {
          _id: '$status',
          count: { $sum: 1 },
        },
      },
    ];
    const byStatus = await UserBid.aggregate(pipeline);

    const successLike = ['offer', 'accepted'];
    const failLike = ['rejected', 'withdrawn'];

    let success = 0;
    let fail = 0;
    const breakdown = {};
    for (const row of byStatus) {
      breakdown[row._id] = row.count;
      if (successLike.includes(row._id)) success += row.count;
      if (failLike.includes(row._id)) fail += row.count;
    }

    return res.json({
      from: from.toISOString(),
      to: to.toISOString(),
      breakdown,
      approxSuccess: success,
      approxFailure: fail,
    });
  }
);

/** Interview stats: HR vs Tech (TECH_1–3 combined) counts in range */
r.get(
  '/groups/:groupId/stats/interviews',
  param('groupId').isMongoId(),
  query('range').optional().isIn(['week', 'month', 'custom']),
  query('from').optional().isISO8601(),
  query('to').optional().isISO8601(),
  query('userIds').optional().isString(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const strict = parseWindowStrict(req.query);
    const { from, to } = strict || parseRange(req.query);
    const groupId = new mongoose.Types.ObjectId(req.params.groupId);
    const base = { groupId, ...bidMemberFilter(req.query) };
    const match = mergeInterviewMatchWithWindow(base, from, to);

    const rows = await Interview.aggregate([
      { $match: match },
      {
        $match: {
          interviewType: { $in: ['HR', 'TECH_1', 'TECH_2', 'TECH_3', 'ASSESSMENT'] },
        },
      },
      {
        $project: {
          bucket: {
            $switch: {
              branches: [
                { case: { $eq: ['$interviewType', 'HR'] }, then: 'HR' },
                {
                  case: { $in: ['$interviewType', ['TECH_1', 'TECH_2', 'TECH_3']] },
                  then: 'TECH',
                },
                { case: { $eq: ['$interviewType', 'ASSESSMENT'] }, then: 'ASSESSMENT' },
              ],
              default: 'OTHER',
            },
          },
        },
      },
      {
        $group: {
          _id: '$bucket',
          count: { $sum: 1 },
        },
      },
    ]);

    const out = { HR: 0, TECH: 0, ASSESSMENT: 0 };
    for (const row of rows) {
      if (row._id === 'HR') out.HR = row.count;
      if (row._id === 'TECH') out.TECH = row.count;
      if (row._id === 'ASSESSMENT') out.ASSESSMENT = row.count;
    }
    return res.json({ from: from.toISOString(), to: to.toISOString(), ...out });
  }
);

/** One call: links created, bids, interviews (same time window as interview panel). */
r.get(
  '/groups/:groupId/stats/summary',
  param('groupId').isMongoId(),
  query('from').notEmpty().isISO8601(),
  query('to').notEmpty().isISO8601(),
  query('userIds').optional().isString(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const win = parseWindowStrict(req.query);
    if (!win) return res.status(400).json({ error: 'Invalid from/to window' });
    const { from, to } = win;
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const groupId = new mongoose.Types.ObjectId(req.params.groupId);
    const bidM = { groupId, ...bidMemberFilter(req.query) };
    const linkM = { groupId, ...linkCreatorFilter(req.query), createdAt: { $gte: from, $lt: to } };
    const ivBase = { groupId, ...bidMemberFilter(req.query) };
    const ivMatch = mergeInterviewMatchWithWindow(ivBase, from, to);

    const [
      linksCreated,
      bidsCreatedInRange,
      bidsByStatus,
      ivTotal,
      ivByStatus,
      ivByTypeBucket,
    ] = await Promise.all([
      GroupLink.countDocuments(linkM),
      UserBid.countDocuments({ ...bidM, createdAt: { $gte: from, $lt: to } }),
      UserBid.aggregate([
        { $match: { ...bidM, updatedAt: { $gte: from, $lt: to } } },
        { $group: { _id: '$status', count: { $sum: 1 } } },
      ]),
      Interview.countDocuments(ivMatch),
      Interview.aggregate([{ $match: ivMatch }, { $group: { _id: '$status', count: { $sum: 1 } } }]),
      Interview.aggregate([
        { $match: ivMatch },
        {
          $project: {
            bucket: {
              $switch: {
                branches: [
                  { case: { $eq: ['$interviewType', 'HR'] }, then: 'HR' },
                  {
                    case: { $in: ['$interviewType', ['TECH_1', 'TECH_2', 'TECH_3']] },
                    then: 'TECH',
                  },
                  { case: { $eq: ['$interviewType', 'ASSESSMENT'] }, then: 'ASSESSMENT' },
                ],
                default: 'OTHER',
              },
            },
          },
        },
        { $group: { _id: '$bucket', count: { $sum: 1 } } },
      ]),
    ]);

    const byStatus = {};
    let bidsUpdatedInRange = 0;
    for (const row of bidsByStatus) {
      const k = row._id ?? 'unknown';
      byStatus[k] = row.count;
      bidsUpdatedInRange += row.count;
    }

    const offerLike = ['offer', 'accepted'];
    const failLike = ['rejected', 'withdrawn'];
    let bidOfferLike = 0;
    let bidNegativeLike = 0;
    for (const k of Object.keys(byStatus)) {
      if (offerLike.includes(k)) bidOfferLike += byStatus[k];
      if (failLike.includes(k)) bidNegativeLike += byStatus[k];
    }

    const interviewStatusCounts = {};
    for (const row of ivByStatus) {
      interviewStatusCounts[row._id ?? 'unknown'] = row.count;
    }

    const passed = interviewStatusCounts.passed ?? 0;
    const failed = interviewStatusCounts.failed ?? 0;
    const decided = passed + failed;
    const passRate = decided ? passed / decided : null;
    const failureRate = decided ? failed / decided : null;

    const byInterviewType = { HR: 0, TECH: 0, ASSESSMENT: 0, OTHER: 0 };
    for (const row of ivByTypeBucket) {
      if (row._id === 'HR') byInterviewType.HR = row.count;
      else if (row._id === 'TECH') byInterviewType.TECH = row.count;
      else if (row._id === 'ASSESSMENT') byInterviewType.ASSESSMENT = row.count;
      else if (row._id === 'OTHER') byInterviewType.OTHER = row.count;
    }

    return res.json({
      from: from.toISOString(),
      to: to.toISOString(),
      links: { created: linksCreated },
      bids: {
        createdInRange: bidsCreatedInRange,
        updatedInRange: bidsUpdatedInRange,
        byStatus,
        offerLike: bidOfferLike,
        negativeLike: bidNegativeLike,
      },
      interviews: {
        totalInRange: ivTotal,
        byStatus: interviewStatusCounts,
        byType: byInterviewType,
        passed,
        failed,
        decided,
        passRate,
        failureRate,
      },
    });
  }
);

/** All members in group (for chart user filter). */
r.get('/groups/:groupId/members', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const m = await assertGroupMember(req.user.id, req.params.groupId);
  if (!m.ok) return res.status(m.status).json({ error: m.error });
  const g = await Group.findById(req.params.groupId).lean();
  const ids = [...g.memberIds, g.creatorId].map(String);
  const unique = [...new Set(ids)];
  const users = await User.find({ _id: { $in: unique } })
    .select('nickname email')
    .lean();
  return res.json({ users });
});

/**
 * Per-member leaderboard for a half-open window [from, to) — use the same bounds as the interview panel.
 * Bids: `byStatus` counts rows with updatedAt in range (current status). `bidsCreatedInRange` uses createdAt.
 * Interviews: same time rules as GET /interviews (scheduled in range or outcome logged in range).
 */
r.get(
  '/groups/:groupId/overview/bids',
  param('groupId').isMongoId(),
  query('from').notEmpty().isISO8601(),
  query('to').notEmpty().isISO8601(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const win = parseWindowStrict(req.query);
    if (!win) return res.status(400).json({ error: 'Invalid from/to window' });
    const { from, to } = win;
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const groupId = new mongoose.Types.ObjectId(req.params.groupId);

    const g = await Group.findById(req.params.groupId).lean();
    const memberIdStrs = [...new Set([...(g.memberIds || []).map(String), String(g.creatorId)])];

    const ivWindow = mergeInterviewMatchWithWindow({ groupId }, from, to);
    const [bidStatusRows, bidCreatedRows, linkRows, ivRows, asmtRows] = await Promise.all([
      UserBid.aggregate([
        { $match: { groupId, updatedAt: { $gte: from, $lt: to } } },
        { $group: { _id: { userId: '$userId', status: '$status' }, count: { $sum: 1 } } },
      ]),
      UserBid.aggregate([
        { $match: { groupId, createdAt: { $gte: from, $lt: to } } },
        { $group: { _id: '$userId', count: { $sum: 1 } } },
      ]),
      GroupLink.aggregate([
        { $match: { groupId, createdAt: { $gte: from, $lt: to } } },
        { $group: { _id: '$createdByUserId', count: { $sum: 1 } } },
      ]),
      Interview.aggregate([
        { $match: { ...ivWindow, interviewType: { $ne: 'ASSESSMENT' } } },
        { $group: { _id: { userId: '$userId', status: '$status' }, count: { $sum: 1 } } },
      ]),
      Interview.aggregate([
        { $match: { ...ivWindow, interviewType: 'ASSESSMENT' } },
        { $group: { _id: { userId: '$userId', status: '$status' }, count: { $sum: 1 } } },
      ]),
    ]);

    const byUserStatus = new Map();
    for (const row of bidStatusRows) {
      const uid = String(row._id.userId);
      if (!byUserStatus.has(uid)) byUserStatus.set(uid, {});
      byUserStatus.get(uid)[row._id.status] = row.count;
    }

    const createdByUser = new Map();
    for (const row of bidCreatedRows) {
      createdByUser.set(String(row._id), row.count);
    }

    const linksByUser = new Map();
    for (const row of linkRows) {
      linksByUser.set(String(row._id), row.count);
    }

    const ivByUser = new Map();
    for (const row of ivRows) {
      const uid = String(row._id.userId);
      const st = row._id.status;
      if (!ivByUser.has(uid)) {
        ivByUser.set(uid, { total: 0, passed: 0, failed: 0 });
      }
      const o = ivByUser.get(uid);
      o.total += row.count;
      if (st === 'passed') o.passed += row.count;
      if (st === 'failed') o.failed += row.count;
    }

    const asmtByUser = new Map();
    for (const row of asmtRows) {
      const uid = String(row._id.userId);
      const st = row._id.status;
      if (!asmtByUser.has(uid)) {
        asmtByUser.set(uid, { total: 0, passed: 0, failed: 0 });
      }
      const o = asmtByUser.get(uid);
      o.total += row.count;
      if (st === 'passed') o.passed += row.count;
      if (st === 'failed') o.failed += row.count;
    }

    function bidsTouched(byStatus) {
      return Object.values(byStatus).reduce((a, b) => a + b, 0);
    }

    const users = await User.find({
      _id: { $in: memberIdStrs.map((id) => new mongoose.Types.ObjectId(id)) },
    })
      .select('nickname email')
      .lean();

    const summary = users.map((u) => {
      const uid = String(u._id);
      const byStatus = { ...(byUserStatus.get(uid) || {}) };
      const iv = ivByUser.get(uid) || { total: 0, passed: 0, failed: 0 };
      const asmt = asmtByUser.get(uid) || { total: 0, passed: 0, failed: 0 };
      const decided = iv.passed + iv.failed;
      const asmtDecided = asmt.passed + asmt.failed;
      return {
        user: { id: uid, nickname: u.nickname, email: u.email },
        linksCreated: linksByUser.get(uid) ?? 0,
        bidsCreatedInRange: createdByUser.get(uid) ?? 0,
        bidsTouchedInRange: bidsTouched(byStatus),
        byStatus,
        interviewsInRange: iv.total,
        interviewsPassed: iv.passed,
        interviewsFailed: iv.failed,
        interviewPassRate: decided ? iv.passed / decided : null,
        assessmentsInRange: asmt.total,
        assessmentsPassed: asmt.passed,
        assessmentsFailed: asmt.failed,
        assessmentPassRate: asmtDecided ? asmt.passed / asmtDecided : null,
      };
    });

    summary.sort((a, b) => (a.user.nickname || '').localeCompare(b.user.nickname || ''));

    return res.json({
      from: from.toISOString(),
      to: to.toISOString(),
      summary,
    });
  }
);

export default r;
