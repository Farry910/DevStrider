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
    const [bidStatusRows, bidCreatedRows, linkRows, ivRows, asmtRows, ivByTypeRows] =
      await Promise.all([
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
        /**
         * Per-interview-type breakdown grouped into the four buckets the overview table cares
         * about: phone_screening (PHONE_SCREENING + legacy HR), interview (TECH_1..3 + CLIENT),
         * assessment, offer. Status restricted to passed/failed for outcome counts.
         */
        Interview.aggregate([
          { $match: ivWindow },
          {
            $project: {
              userId: 1,
              status: 1,
              bucket: {
                $switch: {
                  branches: [
                    {
                      case: { $in: ['$interviewType', ['PHONE_SCREENING', 'HR']] },
                      then: 'phone_screening',
                    },
                    {
                      case: { $in: ['$interviewType', ['TECH_1', 'TECH_2', 'TECH_3', 'CLIENT']] },
                      then: 'interview',
                    },
                    { case: { $eq: ['$interviewType', 'ASSESSMENT'] }, then: 'assessment' },
                    { case: { $eq: ['$interviewType', 'OFFER'] }, then: 'offer' },
                  ],
                  default: 'other',
                },
              },
            },
          },
          {
            $group: {
              _id: { userId: '$userId', bucket: '$bucket', status: '$status' },
              count: { $sum: 1 },
            },
          },
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

    /** Per-user, per-bucket interview outcome counts. Buckets: phone_screening, interview, assessment, offer. */
    const ivByUserType = new Map();
    for (const row of ivByTypeRows) {
      const uid = String(row._id.userId);
      const bucket = row._id.bucket;
      const status = row._id.status;
      if (!ivByUserType.has(uid)) ivByUserType.set(uid, {});
      const userBuckets = ivByUserType.get(uid);
      if (!userBuckets[bucket]) {
        userBuckets[bucket] = { total: 0, passed: 0, failed: 0 };
      }
      userBuckets[bucket].total += row.count;
      if (status === 'passed') userBuckets[bucket].passed += row.count;
      if (status === 'failed') userBuckets[bucket].failed += row.count;
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
      const byInterviewType = ivByUserType.get(uid) || {};
      const empty = { total: 0, passed: 0, failed: 0 };
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
        /** New per-bucket breakdown for the slim overview table. */
        byInterviewType: {
          phone_screening: byInterviewType.phone_screening || empty,
          interview: byInterviewType.interview || empty,
          assessment: byInterviewType.assessment || empty,
          offer: byInterviewType.offer || empty,
        },
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

/**
 * Time-series for the overview chart. Counts bucket per UTC day; rates bucket per ISO week (last 8).
 *
 * Metrics:
 *   - applied                — group's bids that moved to 'applied' (daily)
 *   - interviews_from_bidders — interviews where userId is a group member with roles.bidder (daily)
 *   - interviews_from_callers — interviews where userId is in the union of all callers' watches (daily)
 *   - pass_rate_from_callers  — % passed of resolved interviews scoped to callers' watched bidders (weekly)
 *   - catch_rate_from_bidders — interviews_scheduled / applied_bids for bidders' rows (weekly)
 */
r.get(
  '/groups/:groupId/overview/chart',
  param('groupId').isMongoId(),
  query('metric').isIn([
    'applied',
    'interviews_from_bidders',
    'interviews_from_callers',
    'pass_rate_from_callers',
    'catch_rate_from_bidders',
  ]),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const groupId = new mongoose.Types.ObjectId(req.params.groupId);
    const metric = String(req.query.metric);

    /** Rate metrics bucket weekly (last 8 ISO weeks); count metrics bucket daily (last 7 days). */
    const isRate = metric.startsWith('pass_rate_') || metric.startsWith('catch_rate_');
    const now = new Date();
    const endOfTodayUtc = new Date(
      Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1, 0, 0, 0)
    );

    /** Resolve "bidders" and "callers' watched bidders" once per request from the group's members[]. */
    const g = await Group.findById(req.params.groupId).select('members creatorId').lean();
    const bidderIds = new Set();
    const callerWatchUnion = new Set();
    for (const mem of g?.members || []) {
      const roles = mem.roles || [];
      const uid = String(mem.userId);
      if (roles.includes('bidder')) bidderIds.add(uid);
      if (roles.includes('caller')) {
        for (const w of mem.watches || []) callerWatchUnion.add(String(w));
      }
    }
    /** Group creator counts as bidder + admin, but for chart scoping we treat them as bidder too. */
    if (g?.creatorId) bidderIds.add(String(g.creatorId));
    const toOid = (id) => new mongoose.Types.ObjectId(id);
    const bidderOids = [...bidderIds].map(toOid);
    const callerWatchOids = [...callerWatchUnion].map(toOid);

    function emptyDaily(days) {
      const start = new Date(endOfTodayUtc.getTime() - days * 86400000);
      const out = [];
      for (let i = 0; i < days; i++) {
        const d = new Date(start.getTime() + i * 86400000);
        const key = `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
        out.push({ day: key, value: 0 });
      }
      return { points: out, start, end: endOfTodayUtc };
    }

    /** Week buckets: end at the start of next ISO week; produce 8 buckets ending now. */
    function emptyWeekly(weeks) {
      const start = new Date(endOfTodayUtc.getTime() - weeks * 7 * 86400000);
      const out = [];
      for (let i = 0; i < weeks; i++) {
        const d = new Date(start.getTime() + i * 7 * 86400000);
        const key = `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
        out.push({ day: key, value: 0 });
      }
      return { points: out, start, end: endOfTodayUtc };
    }

    function dayBucket(dateExpr) {
      return { $dateToString: { format: '%Y-%m-%d', date: dateExpr, timezone: 'UTC' } };
    }

    /** Map a date into the start-of-its-bucket day key (matching emptyWeekly's keys). */
    function bucketKeyForWeekly(date, start) {
      const ms = date.getTime() - start.getTime();
      const idx = Math.floor(ms / (7 * 86400000));
      const d = new Date(start.getTime() + idx * 7 * 86400000);
      return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
    }

    if (metric === 'applied') {
      const win = emptyDaily(7);
      const byDay = new Map(win.points.map((p) => [p.day, p]));
      const rows = await UserBid.aggregate([
        { $match: { groupId, status: 'applied', updatedAt: { $gte: win.start, $lt: win.end } } },
        { $group: { _id: dayBucket('$updatedAt'), count: { $sum: 1 } } },
      ]);
      for (const r of rows) {
        const p = byDay.get(r._id);
        if (p) p.value = r.count;
      }
      return res.json({ metric, bucket: 'day', from: win.start, to: win.end, points: win.points });
    }

    if (metric === 'interviews_from_bidders' || metric === 'interviews_from_callers') {
      const scopeOids = metric === 'interviews_from_bidders' ? bidderOids : callerWatchOids;
      const win = emptyDaily(7);
      const byDay = new Map(win.points.map((p) => [p.day, p]));
      if (scopeOids.length > 0) {
        const rows = await Interview.aggregate([
          {
            $match: {
              groupId,
              userId: { $in: scopeOids },
              scheduledDate: { $ne: null, $gte: win.start, $lt: win.end },
            },
          },
          { $group: { _id: dayBucket('$scheduledDate'), count: { $sum: 1 } } },
        ]);
        for (const r of rows) {
          const p = byDay.get(r._id);
          if (p) p.value = r.count;
        }
      }
      return res.json({ metric, bucket: 'day', from: win.start, to: win.end, points: win.points });
    }

    if (metric === 'pass_rate_from_callers') {
      const win = emptyWeekly(8);
      const byKey = new Map(win.points.map((p) => [p.day, p]));
      if (callerWatchOids.length > 0) {
        const rows = await Interview.find({
          groupId,
          userId: { $in: callerWatchOids },
          status: { $in: ['passed', 'failed'] },
          updatedAt: { $gte: win.start, $lt: win.end },
        })
          .select('updatedAt status')
          .lean();
        const buckets = new Map();
        for (const iv of rows) {
          const key = bucketKeyForWeekly(new Date(iv.updatedAt), win.start);
          if (!buckets.has(key)) buckets.set(key, { passed: 0, decided: 0 });
          const b = buckets.get(key);
          b.decided += 1;
          if (iv.status === 'passed') b.passed += 1;
        }
        for (const [key, b] of buckets) {
          const p = byKey.get(key);
          if (p) p.value = b.decided ? b.passed / b.decided : 0;
        }
      }
      return res.json({ metric, bucket: 'week', from: win.start, to: win.end, points: win.points });
    }

    if (metric === 'catch_rate_from_bidders') {
      const win = emptyWeekly(8);
      const byKey = new Map(win.points.map((p) => [p.day, p]));
      if (bidderOids.length > 0) {
        const [appliedRows, ivRows] = await Promise.all([
          UserBid.find({
            groupId,
            userId: { $in: bidderOids },
            status: 'applied',
            updatedAt: { $gte: win.start, $lt: win.end },
          })
            .select('updatedAt')
            .lean(),
          Interview.find({
            groupId,
            userId: { $in: bidderOids },
            scheduledDate: { $ne: null, $gte: win.start, $lt: win.end },
          })
            .select('scheduledDate')
            .lean(),
        ]);
        const buckets = new Map();
        for (const b of appliedRows) {
          const key = bucketKeyForWeekly(new Date(b.updatedAt), win.start);
          if (!buckets.has(key)) buckets.set(key, { applied: 0, interviews: 0 });
          buckets.get(key).applied += 1;
        }
        for (const iv of ivRows) {
          const key = bucketKeyForWeekly(new Date(iv.scheduledDate), win.start);
          if (!buckets.has(key)) buckets.set(key, { applied: 0, interviews: 0 });
          buckets.get(key).interviews += 1;
        }
        for (const [key, b] of buckets) {
          const p = byKey.get(key);
          if (p) p.value = b.applied > 0 ? b.interviews / b.applied : 0;
        }
      }
      return res.json({ metric, bucket: 'week', from: win.start, to: win.end, points: win.points });
    }

    return res.status(400).json({ error: 'Unknown metric' });
  }
);

export default r;
