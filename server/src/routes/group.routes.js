import { Router } from 'express';
import { body, param, validationResult } from 'express-validator';
import mongoose from 'mongoose';
import { Group } from '../models/Group.js';
import { User } from '../models/User.js';
import { JoinRequest } from '../models/JoinRequest.js';
import { GroupLink } from '../models/GroupLink.js';
import { UserBid } from '../models/UserBid.js';
import { Interview } from '../models/Interview.js';
import { requireAuth } from '../middleware/auth.js';
import { assertGroupCreator, assertGroupMember } from '../services/membership.js';
import { mergeOverviewWeights } from '../constants/overviewScoreWeights.js';
import { mergeGroupTimers } from '../constants/groupTimers.js';
import { getProfileBadgeType } from '../constants/profileBadgeTypes.js';
import {
  approveProfileBadgeRequest,
  createProfileBadgeRequest,
  getProfileBadgeSummaryForUserInGroup,
  listPendingProfileBadgeRequestsForGroup,
  rejectProfileBadgeRequest,
} from '../services/profileBadgeService.js';
import { ProfileBadgeRequest } from '../models/ProfileBadgeRequest.js';
import { Feedback } from '../models/Feedback.js';
import { BidAssistantActivity } from '../models/BidAssistantActivity.js';

const r = Router();
r.use(requireAuth);

async function deleteGroupCascade(groupId) {
  const gid = groupId instanceof mongoose.Types.ObjectId ? groupId : new mongoose.Types.ObjectId(groupId);
  await UserBid.deleteMany({ groupId: gid });
  await Interview.deleteMany({ groupId: gid });
  await GroupLink.deleteMany({ groupId: gid });
  await BidAssistantActivity.deleteMany({ groupId: gid });
  await JoinRequest.deleteMany({ groupId: gid });
  await ProfileBadgeRequest.deleteMany({ groupId: gid });
  await Feedback.deleteMany({ groupId: gid });
  await User.updateMany(
    { 'profileBadgeGrants.groupId': gid },
    { $pull: { profileBadgeGrants: { groupId: gid } } }
  );
  await Group.deleteOne({ _id: gid });
}

r.get('/', async (req, res) => {
  const groups = await Group.find({
    $or: [{ memberIds: req.user.id }, { creatorId: req.user.id }],
  })
    .sort({ createdAt: -1 })
    .lean();
  return res.json({ groups });
});

r.get('/all', async (_req, res) => {
  const groups = await Group.find({}).sort({ name: 1 }).lean();
  return res.json({ groups });
});

r.post(
  '/',
  body('name').trim().isLength({ min: 1, max: 120 }),
  body('locationKey').trim().isLength({ min: 1, max: 64 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const { name, locationKey } = req.body;
    const g = await Group.create({
      name,
      locationKey: String(locationKey).toLowerCase(),
      creatorId: req.user.id,
      memberIds: [req.user.id],
    });
    return res.status(201).json({ group: g });
  }
);

r.post(
  '/:groupId/join-request',
  param('groupId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const group = await Group.findById(req.params.groupId);
    if (!group) return res.status(404).json({ error: 'Group not found' });
    const uid = req.user.id;
    if (group.memberIds.some((id) => String(id) === String(uid))) {
      return res.status(400).json({ error: 'Already a member' });
    }
    const jr = await JoinRequest.findOneAndUpdate(
      { groupId: group._id, userId: uid },
      { $set: { status: 'pending' } },
      { upsert: true, new: true, setDefaultsOnInsert: true }
    );
    return res.status(201).json({ joinRequest: jr });
  }
);

r.get('/:groupId/pending-requests', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const cr = await assertGroupCreator(req.user.id, req.params.groupId);
  if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
  const pending = await JoinRequest.find({
    groupId: req.params.groupId,
    status: 'pending',
  })
    .populate('userId', 'email nickname')
    .lean();
  return res.json({ pending });
});

r.post(
  '/:groupId/join-requests/:requestId/approve',
  param('groupId').isMongoId(),
  param('requestId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const jr = await JoinRequest.findOne({
      _id: req.params.requestId,
      groupId: req.params.groupId,
      status: 'pending',
    });
    if (!jr) return res.status(404).json({ error: 'Request not found' });
    jr.status = 'approved';
    await jr.save();
    await Group.findByIdAndUpdate(req.params.groupId, {
      $addToSet: { memberIds: jr.userId },
    });
    return res.json({ ok: true });
  }
);

r.post(
  '/:groupId/join-requests/:requestId/reject',
  param('groupId').isMongoId(),
  param('requestId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const jr = await JoinRequest.findOne({
      _id: req.params.requestId,
      groupId: req.params.groupId,
      status: 'pending',
    });
    if (!jr) return res.status(404).json({ error: 'Request not found' });
    jr.status = 'rejected';
    await jr.save();
    return res.json({ ok: true });
  }
);

/**
 * Group owner only: set overview total score weights (partial or full object; merged with defaults).
 */
r.patch(
  '/:groupId/overview-score-weights',
  param('groupId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const body = req.body;
    if (!body || typeof body !== 'object' || Array.isArray(body)) {
      return res.status(400).json({ error: 'Expected a JSON object of weight fields' });
    }
    const merged = mergeOverviewWeights(body);
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    g.overviewScoreWeights = merged;
    await g.save();
    return res.json({ overviewScoreWeights: merged });
  }
);

/**
 * Group owner: timers for junk auto-removal grace, bid duplicate lookback, reserved minutes.
 */
r.patch(
  '/:groupId/timers',
  param('groupId').isMongoId(),
  body('junkRemovalGraceMinutes').optional().isInt({ min: 1, max: 10080 }),
  body('bidDuplicateLookbackDays').optional().isInt({ min: 1, max: 3650 }),
  body('possibleTimerMinutes').optional().isInt({ min: 0, max: 10080 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    g.timers = mergeGroupTimers(g.timers, req.body);
    await g.save();
    const resolved = mergeGroupTimers(g.timers, {});
    return res.json({ timers: resolved });
  }
);

r.get('/:groupId/me', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const g = await Group.findById(req.params.groupId).lean();
  if (!g) return res.status(404).json({ error: 'Group not found' });
  const uid = String(req.user.id);
  const isMember = g.memberIds.some((id) => String(id) === uid);
  const isCreator = String(g.creatorId) === uid;
  const assisterId = g.removalAssisterId ? String(g.removalAssisterId) : null;
  const timers = mergeGroupTimers(g.timers, {});
  return res.json({
    group: { ...g, timers },
    role: isCreator ? 'creator' : isMember ? 'member' : 'none',
    isMember,
    removal: {
      assisterUserId: assisterId,
      ownerConfirmedAt: g.removalOwnerConfirmedAt,
      assisterConfirmedAt: g.removalAssisterConfirmedAt,
    },
  });
});

r.get('/:groupId/me/profile-badges-summary', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const result = await getProfileBadgeSummaryForUserInGroup(req.user.id, req.params.groupId);
  if (!result.ok) return res.status(result.status).json({ error: result.error });
  const requests = result.requests.map((row) => ({
    id: row._id,
    badgeKey: row.badgeKey,
    badgeType: getProfileBadgeType(row.badgeKey),
    status: row.status,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
    reviewedAt: row.reviewedAt,
    reviewNote: row.reviewNote || '',
  }));
  return res.json({ approvedKeys: result.approvedKeys, requests });
});

r.post(
  '/:groupId/me/profile-badge-requests',
  param('groupId').isMongoId(),
  body('badgeKey').trim().isLength({ min: 1, max: 64 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const { badgeKey } = req.body;
    const result = await createProfileBadgeRequest(req.user.id, req.params.groupId, badgeKey);
    if (!result.ok) return res.status(result.status).json({ error: result.error });
    return res.status(201).json({ request: result.request });
  }
);

r.get('/:groupId/pending-profile-badge-requests', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const cr = await assertGroupCreator(req.user.id, req.params.groupId);
  if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
  const rows = await listPendingProfileBadgeRequestsForGroup(req.params.groupId);
  const pending = rows.map((row) => ({
    id: row._id,
    badgeKey: row.badgeKey,
    badgeType: getProfileBadgeType(row.badgeKey),
    status: row.status,
    createdAt: row.createdAt,
    user: row.userId
      ? {
          id: row.userId._id,
          email: row.userId.email,
          nickname: row.userId.nickname,
        }
      : null,
  }));
  return res.json({ pending });
});

r.post(
  '/:groupId/profile-badge-requests/:requestId/approve',
  param('groupId').isMongoId(),
  param('requestId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const result = await approveProfileBadgeRequest(
      req.params.requestId,
      req.user.id,
      req.params.groupId
    );
    if (!result.ok) return res.status(result.status).json({ error: result.error });
    return res.json({ ok: true, request: result.request });
  }
);

r.post(
  '/:groupId/profile-badge-requests/:requestId/reject',
  param('groupId').isMongoId(),
  param('requestId').isMongoId(),
  body('note').optional().trim().isLength({ max: 500 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const note = req.body.note ?? '';
    const result = await rejectProfileBadgeRequest(
      req.params.requestId,
      req.user.id,
      req.params.groupId,
      note
    );
    if (!result.ok) return res.status(result.status).json({ error: result.error });
    return res.json({ ok: true, request: result.request });
  }
);

/** Creator only: rename or change location key. */
r.patch(
  '/:groupId',
  param('groupId').isMongoId(),
  body('name').optional().trim().isLength({ min: 1, max: 120 }),
  body('locationKey').optional().trim().isLength({ min: 1, max: 64 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    if (req.body.name === undefined && req.body.locationKey === undefined) {
      return res.status(400).json({ error: 'Provide at least one of name, locationKey' });
    }
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    if (req.body.name !== undefined) g.name = req.body.name;
    if (req.body.locationKey !== undefined) g.locationKey = String(req.body.locationKey).toLowerCase();
    try {
      await g.save();
    } catch (e) {
      if (e && e.code === 11000) {
        return res.status(409).json({ error: 'A group with this name and location already exists' });
      }
      throw e;
    }
    return res.json({ group: g.toObject() });
  }
);

/** Creator only: set or clear removal assister (must be another group member). Clears in-progress removal confirmations. */
r.patch(
  '/:groupId/removal-assister',
  param('groupId').isMongoId(),
  body('userId')
    .optional({ nullable: true })
    .custom((v) => v === null || v === undefined || v === '' || mongoose.isValidObjectId(String(v))),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });

    const raw = req.body.userId;
    const newAssister = raw == null || raw === '' ? null : String(raw);

    if (newAssister && String(newAssister) === String(g.creatorId)) {
      return res.status(400).json({ error: 'The owner cannot be the removal assister' });
    }
    if (newAssister) {
      const okMember = g.memberIds.some((id) => String(id) === String(newAssister));
      if (!okMember) {
        return res.status(400).json({ error: 'Removal assister must be a current group member' });
      }
    }

    g.removalAssisterId = newAssister;
    g.removalOwnerConfirmedAt = null;
    g.removalAssisterConfirmedAt = null;
    await g.save();
    return res.json({ group: g.toObject() });
  }
);

/**
 * Two-party removal when `removalAssisterId` is set: owner and assister must each call this.
 * With no assister, only the owner may call; deletion completes immediately.
 */
r.post('/:groupId/removal-request', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const g = await Group.findById(req.params.groupId);
  if (!g) return res.status(404).json({ error: 'Group not found' });
  const uid = String(req.user.id);
  const isOwner = String(g.creatorId) === uid;
  const assisterId = g.removalAssisterId ? String(g.removalAssisterId) : null;
  const isAssister = Boolean(assisterId && assisterId === uid);

  if (!assisterId) {
    if (!isOwner) {
      return res.status(403).json({ error: 'Only the group owner can delete this group' });
    }
    await deleteGroupCascade(g._id);
    return res.json({ completed: true });
  }

  if (!isOwner && !isAssister) {
    return res.status(403).json({ error: 'Only the owner or removal assister can confirm deletion' });
  }

  if (isOwner) g.removalOwnerConfirmedAt = new Date();
  if (isAssister) g.removalAssisterConfirmedAt = new Date();

  const ownerDone = Boolean(g.removalOwnerConfirmedAt);
  const assisterDone = Boolean(g.removalAssisterConfirmedAt);

  if (ownerDone && assisterDone) {
    await deleteGroupCascade(g._id);
    return res.json({ completed: true });
  }

  await g.save();
  return res.json({
    completed: false,
    ownerConfirmed: ownerDone,
    assisterConfirmed: assisterDone,
  });
});

/** Owner only: abort a pending two-party removal (clears confirmation timestamps). */
r.post('/:groupId/removal-request/cancel', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const cr = await assertGroupCreator(req.user.id, req.params.groupId);
  if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
  const g = await Group.findById(req.params.groupId);
  if (!g) return res.status(404).json({ error: 'Group not found' });
  g.removalOwnerConfirmedAt = null;
  g.removalAssisterConfirmedAt = null;
  await g.save();
  return res.json({ ok: true, group: g.toObject() });
});

/**
 * Owner-only instant delete when no removal assister is configured.
 * If an assister is set, use POST /removal-request from both parties instead.
 */
r.delete('/:groupId', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const cr = await assertGroupCreator(req.user.id, req.params.groupId);
  if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
  const g = await Group.findById(req.params.groupId).lean();
  if (!g) return res.status(404).json({ error: 'Group not found' });
  if (g.removalAssisterId) {
    return res.status(400).json({
      error:
        'This group has a removal assister. Both owner and assister must POST /groups/:id/removal-request to delete.',
      code: 'TWO_PARTY_REMOVAL_REQUIRED',
    });
  }
  await deleteGroupCascade(g._id);
  return res.json({ ok: true });
});

r.post(
  '/:groupId/feedback',
  param('groupId').isMongoId(),
  body('message').trim().isLength({ min: 1, max: 8000 }),
  body('category').optional().isIn(['general', 'bug', 'idea', 'other']),
  body('pagePath').optional().trim().isLength({ max: 512 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const mr = await assertGroupMember(req.user.id, req.params.groupId);
    if (!mr.ok) return res.status(mr.status).json({ error: mr.error });
    const { message, category, pagePath } = req.body;
    const doc = await Feedback.create({
      groupId: req.params.groupId,
      userId: req.user.id,
      message,
      category: category ?? 'general',
      pagePath: typeof pagePath === 'string' ? pagePath : '',
    });
    return res.status(201).json({ feedback: doc.toObject() });
  }
);

r.get('/:groupId/feedback', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const mr = await assertGroupMember(req.user.id, req.params.groupId);
  if (!mr.ok) return res.status(mr.status).json({ error: mr.error });

  const rawLimit = Number.parseInt(String(req.query.limit ?? '80'), 10);
  const limit = Number.isFinite(rawLimit) ? Math.min(150, Math.max(1, rawLimit)) : 80;
  const uid = new mongoose.Types.ObjectId(req.user.id);
  const gid = req.params.groupId;
  const isOwner = String(mr.group.creatorId) === String(req.user.id);

  const filter = isOwner
    ? (() => {
        const q = { groupId: gid };
        const scope = String(req.query.scope ?? 'all');
        if (scope === 'community') q.category = 'general';
        else if (scope === 'private') q.category = { $in: ['bug', 'idea', 'other'] };
        const st = String(req.query.status ?? 'all');
        if (scope === 'private' && (st === 'open' || st === 'resolved' || st === 'ignored')) {
          if (st === 'open') {
            q.$or = [{ status: 'open' }, { status: { $exists: false } }];
          } else {
            q.status = st;
          }
        }
        return q;
      })()
    : {
        groupId: gid,
        $or: [{ category: 'general' }, { category: { $ne: 'general' }, userId: uid }],
      };

  const items = await Feedback.find(filter)
    .sort({ createdAt: -1 })
    .limit(limit)
    .populate('userId', 'nickname email')
    .lean();

  return res.json({ feedback: items, isOwner });
});

r.patch(
  '/:groupId/feedback/:feedbackId',
  param('groupId').isMongoId(),
  param('feedbackId').isMongoId(),
  body('status').optional().isIn(['open', 'resolved', 'ignored']),
  body('ownerComment').optional().trim().isLength({ max: 4000 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });

    const { status: nextStatus, ownerComment: commentRaw } = req.body;
    const hasStatus = nextStatus !== undefined && nextStatus !== null && nextStatus !== '';
    const hasComment = commentRaw !== undefined;

    if (!hasStatus && !hasComment) {
      return res.status(400).json({ error: 'Provide status and/or ownerComment' });
    }

    const fb = await Feedback.findOne({
      _id: req.params.feedbackId,
      groupId: req.params.groupId,
    });
    if (!fb) return res.status(404).json({ error: 'Feedback not found' });

    if (hasStatus) {
      if (fb.category === 'general') {
        return res.status(400).json({ error: 'Status can only be changed for bug, feature, or other feedback' });
      }
      fb.status = nextStatus;
    }

    if (hasComment) {
      const t = typeof commentRaw === 'string' ? commentRaw.trim() : '';
      fb.ownerComment = t;
      fb.ownerCommentAt = t ? new Date() : null;
    }

    await fb.save();
    const out = await Feedback.findById(fb._id).populate('userId', 'nickname email').lean();
    return res.json({ feedback: out });
  }
);

export default r;
