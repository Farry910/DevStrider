import { Router } from 'express';
import { body, param, query, validationResult } from 'express-validator';
import { Interview, INTERVIEW_TYPES, INTERVIEW_ORIGINS } from '../models/Interview.js';
import { UserBid } from '../models/UserBid.js';
import { requireAuth } from '../middleware/auth.js';
import { emitBidBoardInvalidate } from '../socket/hexGameSocket.js';
import { awardAchievementsAsync } from '../services/achievementService.js';
import { assertGroupMember } from '../services/membership.js';
import { norm } from '../services/text.js';
import { canFollow, allowedNextTypes } from '../services/interviewRules.js';
import {
  buildInterviewMatch,
  mergeInterviewMatchWithWindow,
  parseInterviewSort,
} from '../services/interviewList.js';

const r = Router();
r.use(requireAuth);

const MAX_INTERVIEW_LIST = 2000;

/** Depth-first delete so child interviews are removed before parents. */
async function deleteInterviewAndDescendants(interviewId, groupId, userId) {
  const children = await Interview.find({
    parentInterviewId: interviewId,
    groupId,
    userId,
  })
    .select('_id')
    .lean();
  for (const c of children) {
    await deleteInterviewAndDescendants(String(c._id), groupId, userId);
  }
  await Interview.deleteOne({ _id: interviewId, groupId, userId });
}

r.get(
  '/groups/:groupId/interviews',
  param('groupId').isMongoId(),
  query('from').notEmpty().isISO8601(),
  query('to').notEmpty().isISO8601(),
  query('sort').optional().isString(),
  query('f_company').optional().isString(),
  query('f_role').optional().isString(),
  query('f_recruiter').optional().isString(),
  query('f_interviewType').optional().isString(),
  query('f_status').optional().isString(),
  query('f_origin').optional().isString(),
  query('f_meetingLink').optional().isString(),
  query('f_userComment').optional().isString(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const t0 = new Date(req.query.from);
    const t1 = new Date(req.query.to);
    const filters = {
      company: req.query.f_company,
      role: req.query.f_role,
      recruiter: req.query.f_recruiter,
      interviewType: req.query.f_interviewType,
      status: req.query.f_status,
      origin: req.query.f_origin,
      meetingLink: req.query.f_meetingLink,
      userComment: req.query.f_userComment,
    };
    const baseMatch = buildInterviewMatch(req.params.groupId, req.user.id, filters);
    const match = mergeInterviewMatchWithWindow(baseMatch, t0, t1);
    const sortObj = parseInterviewSort(req.query.sort);
    const [total, items] = await Promise.all([
      Interview.countDocuments(match),
      Interview.find(match).sort(sortObj).limit(MAX_INTERVIEW_LIST).lean(),
    ]);

    const allMine = await Interview.find({
      groupId: req.params.groupId,
      userId: req.user.id,
    }).lean();

    const enriched = items.map((row) => {
      let warn = false;
      const earlier = allMine.filter(
        (x) => x.createdAt < row.createdAt && String(x._id) !== String(row._id)
      );
      if (norm(row.company) && norm(row.recruiter)) {
        warn = earlier.some(
          (x) =>
            norm(x.company) === norm(row.company) &&
            norm(x.recruiter) === norm(row.recruiter)
        );
      }
      return { ...row, recruiterCompanyDuplicateWarning: warn };
    });

    return res.json({
      total,
      interviews: enriched,
      from: t0.toISOString(),
      to: t1.toISOString(),
      sort: req.query.sort || 'scheduledDate:desc',
      filters,
      capped: total > MAX_INTERVIEW_LIST,
    });
  }
);

r.post(
  '/groups/:groupId/interviews',
  param('groupId').isMongoId(),
  body('meetingLink').trim().isLength({ min: 3, max: 2048 }),
  body('origin').isIn(INTERVIEW_ORIGINS),
  body('interviewType').isIn(INTERVIEW_TYPES),
  body('bidId').optional().isMongoId(),
  body('parentInterviewId').optional().isMongoId(),
  body('company').optional().isString(),
  body('role').optional().isString(),
  body('recruiter').optional().isString(),
  body('additionalAttendees').optional().isString(),
  body('scheduledDate').optional().isISO8601(),
  body('scheduledTime').optional().isString(),
  body('durationMinutes').optional().isInt({ min: 5, max: 720 }),
  body('status').optional().isIn(['scheduled', 'completed', 'passed', 'failed', 'cancelled']),
  body('userComment').optional().isString(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });

    const {
      meetingLink,
      origin,
      interviewType,
      bidId,
      parentInterviewId,
      company,
      role,
      recruiter,
      additionalAttendees,
      scheduledDate,
      scheduledTime,
      durationMinutes,
      status,
      userComment,
    } = req.body;

    let co = company || '';
    let ro = role || '';
    let bidRef = bidId || null;
    let bidDocForInterview = null;

    if (parentInterviewId) {
      const parent = await Interview.findOne({
        _id: parentInterviewId,
        groupId: req.params.groupId,
        userId: req.user.id,
      });
      if (!parent) return res.status(404).json({ error: 'Parent interview not found' });
      if (!canFollow(parent.interviewType, interviewType)) {
        return res.status(400).json({ error: 'Invalid interview type for this stage' });
      }
      if (!co) co = parent.company || '';
      if (!ro) ro = parent.role || '';
      if (!bidRef && parent.bidId) bidRef = parent.bidId;
    } else if (interviewType !== 'HR') {
      return res.status(400).json({ error: 'First interview in a chain must be HR' });
    }

    if (origin === 'bid') {
      if (!bidRef) {
        return res.status(400).json({
          error:
            'bidId is required when origin is bid, or the parent interview must be tied to a bid',
        });
      }
      const bid = await UserBid.findOne({
        _id: bidRef,
        groupId: req.params.groupId,
        userId: req.user.id,
      });
      if (!bid) return res.status(404).json({ error: 'Bid not found' });
      if (!parentInterviewId) {
        co = bid.company;
        ro = bid.role;
      }
      bidRef = bid._id;
      bidDocForInterview = bid;
    }

    const doc = await Interview.create({
      userId: req.user.id,
      groupId: req.params.groupId,
      meetingLink,
      origin,
      bidId: bidRef,
      interviewType,
      company: co,
      role: ro,
      recruiter: recruiter || '',
      additionalAttendees: additionalAttendees || '',
      scheduledDate: scheduledDate ? new Date(scheduledDate) : null,
      scheduledTime: scheduledTime || '',
      durationMinutes: durationMinutes ?? 60,
      status: status || 'scheduled',
      userComment: userComment || '',
      parentInterviewId: parentInterviewId || null,
    });

    if (origin === 'bid' && bidDocForInterview && !parentInterviewId) {
      bidDocForInterview.status = 'interview';
      bidDocForInterview.lastModifiedBy = req.user.id;
      bidDocForInterview.audit.push({
        userId: req.user.id,
        action: 'interview_created',
        snapshot: { status: 'interview', interviewId: doc._id },
      });
      await bidDocForInterview.save();
    }

    if (origin === 'bid') {
      emitBidBoardInvalidate(req.params.groupId);
    }
    awardAchievementsAsync({
      userId: req.user.id,
      groupId: req.params.groupId,
      kinds: ['weekly_interviews'],
    });

    return res.status(201).json({ interview: doc });
  }
);

r.patch(
  '/groups/:groupId/interviews/:interviewId',
  param('groupId').isMongoId(),
  param('interviewId').isMongoId(),
  body('meetingLink').optional().trim().isLength({ min: 3, max: 2048 }),
  body('origin').optional().isIn(INTERVIEW_ORIGINS),
  body('interviewType').optional().isIn(INTERVIEW_TYPES),
  body('bidId').optional().isMongoId(),
  body('company').optional().isString(),
  body('role').optional().isString(),
  body('recruiter').optional().isString(),
  body('additionalAttendees').optional().isString(),
  body('scheduledDate').optional().isISO8601(),
  body('scheduledTime').optional().isString(),
  body('durationMinutes').optional().isInt({ min: 5, max: 720 }),
  body('status').optional().isIn(['scheduled', 'completed', 'passed', 'failed', 'cancelled']),
  body('userComment').optional().isString(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const iv = await Interview.findOne({
      _id: req.params.interviewId,
      groupId: req.params.groupId,
      userId: req.user.id,
    });
    if (!iv) return res.status(404).json({ error: 'Interview not found' });

    const keys = [
      'meetingLink',
      'origin',
      'interviewType',
      'bidId',
      'company',
      'role',
      'recruiter',
      'additionalAttendees',
      'scheduledTime',
      'durationMinutes',
      'status',
      'userComment',
    ];
    for (const k of keys) {
      if (req.body[k] !== undefined) iv[k] = req.body[k];
    }
    if (req.body.scheduledDate !== undefined) {
      iv.scheduledDate = req.body.scheduledDate ? new Date(req.body.scheduledDate) : null;
    }
    await iv.save();
    return res.json({ interview: iv });
  }
);

r.get(
  '/groups/:groupId/interviews/:interviewId/allowed-next-types',
  param('groupId').isMongoId(),
  param('interviewId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const iv = await Interview.findOne({
      _id: req.params.interviewId,
      groupId: req.params.groupId,
      userId: req.user.id,
    }).lean();
    if (!iv) return res.status(404).json({ error: 'Not found' });
    return res.json({ types: allowedNextTypes(iv.interviewType) });
  }
);

r.delete(
  '/groups/:groupId/interviews/:interviewId',
  param('groupId').isMongoId(),
  param('interviewId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const iv = await Interview.findOne({
      _id: req.params.interviewId,
      groupId: req.params.groupId,
      userId: req.user.id,
    });
    if (!iv) return res.status(404).json({ error: 'Interview not found' });
    await deleteInterviewAndDescendants(req.params.interviewId, req.params.groupId, req.user.id);
    return res.status(204).send();
  }
);

export default r;
