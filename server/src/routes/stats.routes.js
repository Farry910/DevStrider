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
 * Per-user time-series for the overview chart. Returns one series per relevant user (one line per
 * user on the rendered chart). All metrics bucket per UTC day, 7 days total. Rate metrics show
 * each day's rate (0 when there's no decided activity that day).
 *
 * "User" dimension by metric:
 *   - applied                  → per bidder (UserBid.userId)
 *   - interviews_from_bidders  → per bidder (Interview.userId)
 *   - interviews_from_callers  → per caller (each caller's count of interviews in their watch set)
 *   - pass_rate_from_callers   → per caller (each caller's pass rate over their watched bidders)
 *   - catch_rate_from_bidders  → per bidder (each bidder's interviews/applied)
 */
r.get(
  '/groups/:groupId/overview/chart',
  param('groupId').isMongoId(),
  /**
   * Y-axis metric:
   *   - bids        — count of UserBids that hit 'applied', bucketed by updatedAt
   *   - interviews  — count of Interviews, bucketed by chosen interviewBucket field
   *   - catch_rate  — interviews / bids per bucket
   *   - pass_rate   — passed / (passed+failed) per bucket
   *   - fail_rate   — failed / (passed+failed) per bucket
   */
  query('metric').isIn(['bids', 'interviews', 'catch_rate', 'pass_rate', 'fail_rate']),
  /** X-axis granularity. `week` = 7 daily buckets; `month` = 4 weekly buckets. */
  query('xAxis').optional().isIn(['week', 'month']),
  /**
   * Which Interview timestamp to bucket by:
   *   - during — bucket by Interview.createdAt (when the interview was scheduled)
   *   - into   — bucket by Interview.scheduledDate (when the interview takes place)
   * Only affects interview-related metrics (interviews, catch_rate, pass_rate, fail_rate).
   */
  query('interviewBucket').optional().isIn(['during', 'into']),
  /**
   * Viewer-side timezone offset in minutes (signed). E.g. -360 for UTC-6 (Mexico City), +330 for
   * UTC+5:30 (India), 0 for UTC (default). Used to align day buckets to the viewer's calendar day.
   */
  query('tzOffsetMinutes').optional().isInt({ min: -720, max: 840 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const groupId = new mongoose.Types.ObjectId(req.params.groupId);
    const metric = String(req.query.metric);
    const xAxis = req.query.xAxis === 'month' ? 'month' : 'week';
    const interviewBucket = req.query.interviewBucket === 'during' ? 'during' : 'into';
    const tzOffsetMinutes = Number.isFinite(Number(req.query.tzOffsetMinutes))
      ? Number(req.query.tzOffsetMinutes)
      : 0;
    const tzOffsetMs = tzOffsetMinutes * 60 * 1000;

    const now = new Date();
    /**
     * Compute "end of today" in the viewer's local frame, then translate back to UTC for DB
     * queries. The window is [endOfTodayUtc - 7days, endOfTodayUtc) but anchored on the viewer's
     * midnight, not UTC midnight.
     */
    const localNow = new Date(now.getTime() + tzOffsetMs);
    const endOfTodayLocal = new Date(
      Date.UTC(
        localNow.getUTCFullYear(),
        localNow.getUTCMonth(),
        localNow.getUTCDate() + 1,
        0,
        0,
        0
      )
    );
    const endOfTodayUtc = new Date(endOfTodayLocal.getTime() - tzOffsetMs);

    /** Resolve bidder set + per-caller watch lists from the group's members[]. */
    const g = await Group.findById(req.params.groupId).select('members creatorId').lean();
    const bidderIds = new Set();
    /** Map<callerUserId, Set<watchedBidderUserId>> */
    const callerWatches = new Map();
    for (const mem of g?.members || []) {
      const roles = mem.roles || [];
      const uid = String(mem.userId);
      if (roles.includes('bidder')) bidderIds.add(uid);
      if (roles.includes('caller')) {
        callerWatches.set(uid, new Set((mem.watches || []).map(String)));
      }
    }
    /** Group creator is implicit admin/bidder/caller — count as bidder for series purposes. */
    if (g?.creatorId) bidderIds.add(String(g.creatorId));

    const toOid = (id) => new mongoose.Types.ObjectId(id);

    /**
     * Bucket geometry: week = 7 × 1-day buckets; month = 4 × 7-day buckets. Total window length
     * always equals bucketCount × bucketSizeMs ending at endOfTodayUtc.
     */
    const bucketCount = xAxis === 'month' ? 4 : 7;
    const bucketSizeMs = xAxis === 'month' ? 7 * 86400000 : 86400000;
    const windowStart = new Date(endOfTodayUtc.getTime() - bucketCount * bucketSizeMs);
    const bucketKeys = [];
    for (let i = 0; i < bucketCount; i++) {
      /** Each key labels the local day this bucket starts in (so it survives DST/offset gaps). */
      const dLocal = new Date(windowStart.getTime() + tzOffsetMs + i * bucketSizeMs);
      bucketKeys.push(
        `${dLocal.getUTCFullYear()}-${String(dLocal.getUTCMonth() + 1).padStart(2, '0')}-${String(dLocal.getUTCDate()).padStart(2, '0')}`
      );
    }
    function bucketIndexFor(date) {
      const ms = date.getTime() - windowStart.getTime();
      const idx = Math.floor(ms / bucketSizeMs);
      return idx >= 0 && idx < bucketCount ? idx : -1;
    }
    function emptyPointArray() {
      return bucketKeys.map((day) => ({ day, value: 0 }));
    }

    /** Pull nicknames once for whichever set we end up using. */
    async function lookupUsers(idSet) {
      const ids = [...idSet];
      if (ids.length === 0) return new Map();
      const users = await User.find({ _id: { $in: ids.map(toOid) } })
        .select('nickname')
        .lean();
      return new Map(users.map((u) => [String(u._id), u.nickname || '']));
    }

    /** Build a series record from a per-user / per-bucket value map. */
    function buildSeries(userOrder, nicknames, valueMap) {
      return userOrder
        .map((uid) => ({
          userId: uid,
          nickname: nicknames.get(uid) || uid.slice(-6),
          points: bucketKeys.map((day, i) => ({
            day,
            value: valueMap.get(uid)?.[i] ?? 0,
          })),
        }))
        /** Hide users with no data points so the legend stays clean. */
        .filter((s) => s.points.some((p) => p.value !== 0));
    }

    /** All metrics scope by group bidders (interview/bid userId in bidderIds). */
    const userOrder = [...bidderIds];
    const nicknames = await lookupUsers(bidderIds);
    const valueMap = new Map(); // userId -> number[]
    if (userOrder.length === 0) {
      return res.json({
        metric,
        xAxis,
        interviewBucket,
        bucket: xAxis === 'month' ? 'week' : 'day',
        from: windowStart,
        to: endOfTodayUtc,
        buckets: bucketKeys,
        series: [],
      });
    }
    const oids = userOrder.map(toOid);
    /** Bucketing field for interview-related metrics. */
    const ivBucketField = interviewBucket === 'during' ? 'createdAt' : 'scheduledDate';

    function bump(map, uid, idx) {
      if (!map.has(uid)) map.set(uid, new Array(bucketCount).fill(0));
      map.get(uid)[idx] += 1;
    }

    if (metric === 'bids') {
      const rows = await UserBid.find({
        groupId,
        userId: { $in: oids },
        status: 'applied',
        updatedAt: { $gte: windowStart, $lt: endOfTodayUtc },
      })
        .select('userId updatedAt')
        .lean();
      for (const r of rows) {
        const idx = bucketIndexFor(new Date(r.updatedAt));
        if (idx >= 0) bump(valueMap, String(r.userId), idx);
      }
    } else if (metric === 'interviews') {
      const ivMatch = {
        groupId,
        userId: { $in: oids },
        [ivBucketField]: { $gte: windowStart, $lt: endOfTodayUtc },
      };
      if (ivBucketField === 'scheduledDate') ivMatch.scheduledDate.$ne = null;
      const rows = await Interview.find(ivMatch).select(`userId ${ivBucketField}`).lean();
      for (const r of rows) {
        const idx = bucketIndexFor(new Date(r[ivBucketField]));
        if (idx >= 0) bump(valueMap, String(r.userId), idx);
      }
    } else if (metric === 'catch_rate') {
      /** interviews_in_bucket / bids_in_bucket, per bidder. */
      const ivMatch = {
        groupId,
        userId: { $in: oids },
        [ivBucketField]: { $gte: windowStart, $lt: endOfTodayUtc },
      };
      if (ivBucketField === 'scheduledDate') ivMatch.scheduledDate.$ne = null;
      const [appliedRows, ivRows] = await Promise.all([
        UserBid.find({
          groupId,
          userId: { $in: oids },
          status: 'applied',
          updatedAt: { $gte: windowStart, $lt: endOfTodayUtc },
        })
          .select('userId updatedAt')
          .lean(),
        Interview.find(ivMatch).select(`userId ${ivBucketField}`).lean(),
      ]);
      const applied = new Map();
      const interviews = new Map();
      for (const b of appliedRows) {
        const idx = bucketIndexFor(new Date(b.updatedAt));
        if (idx >= 0) bump(applied, String(b.userId), idx);
      }
      for (const iv of ivRows) {
        const idx = bucketIndexFor(new Date(iv[ivBucketField]));
        if (idx >= 0) bump(interviews, String(iv.userId), idx);
      }
      const uidsTouched = new Set([...applied.keys(), ...interviews.keys()]);
      for (const uid of uidsTouched) {
        const a = applied.get(uid) || new Array(bucketCount).fill(0);
        const i = interviews.get(uid) || new Array(bucketCount).fill(0);
        valueMap.set(uid, a.map((aV, idx) => (aV > 0 ? i[idx] / aV : 0)));
      }
    } else if (metric === 'pass_rate' || metric === 'fail_rate') {
      /** numerator / (passed+failed), per bidder, bucketed by chosen interview timestamp. */
      const target = metric === 'pass_rate' ? 'passed' : 'failed';
      const ivMatch = {
        groupId,
        userId: { $in: oids },
        status: { $in: ['passed', 'failed'] },
        [ivBucketField]: { $gte: windowStart, $lt: endOfTodayUtc },
      };
      if (ivBucketField === 'scheduledDate') ivMatch.scheduledDate.$ne = null;
      const rows = await Interview.find(ivMatch)
        .select(`userId status ${ivBucketField}`)
        .lean();
      const matched = new Map();
      const decided = new Map();
      for (const iv of rows) {
        const idx = bucketIndexFor(new Date(iv[ivBucketField]));
        if (idx < 0) continue;
        bump(decided, String(iv.userId), idx);
        if (iv.status === target) bump(matched, String(iv.userId), idx);
      }
      const uidsTouched = new Set(decided.keys());
      for (const uid of uidsTouched) {
        const m = matched.get(uid) || new Array(bucketCount).fill(0);
        const d = decided.get(uid) || new Array(bucketCount).fill(0);
        valueMap.set(uid, d.map((dV, idx) => (dV > 0 ? m[idx] / dV : 0)));
      }
    }

    return res.json({
      metric,
      xAxis,
      interviewBucket,
      bucket: xAxis === 'month' ? 'week' : 'day',
      from: windowStart,
      to: endOfTodayUtc,
      buckets: bucketKeys,
      series: buildSeries(userOrder, nicknames, valueMap),
    });
  }
);

export default r;
