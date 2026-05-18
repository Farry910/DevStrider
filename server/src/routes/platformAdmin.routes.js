import { Router } from 'express';
import { body, param, validationResult } from 'express-validator';
import mongoose from 'mongoose';
import { Group } from '../models/Group.js';
import { User } from '../models/User.js';
import { UserBid } from '../models/UserBid.js';
import { GroupLink } from '../models/GroupLink.js';
import { Interview } from '../models/Interview.js';
import { BidAssistantActivity } from '../models/BidAssistantActivity.js';
import { JoinRequest } from '../models/JoinRequest.js';
import { ProfileBadgeRequest } from '../models/ProfileBadgeRequest.js';
import { Feedback } from '../models/Feedback.js';
import { requireAuth } from '../middleware/auth.js';
import { assertPlatformAdmin } from '../services/membership.js';

const r = Router();
r.use(requireAuth);

/** Every route below requires platformRole === 'admin'. */
r.use(async (req, res, next) => {
  const a = await assertPlatformAdmin(req.user.id);
  if (!a.ok) return res.status(a.status).json({ error: a.error });
  next();
});

/** Pending-approval queue. */
r.get('/pending-groups', async (_req, res) => {
  const groups = await Group.find({ status: 'pending' })
    .sort({ createdAt: -1 })
    .populate('creatorId', 'email nickname')
    .lean();
  return res.json({
    groups: groups.map((g) => ({
      id: g._id,
      name: g.name,
      locationKey: g.locationKey,
      createdAt: g.createdAt,
      creator: g.creatorId
        ? {
            id: g.creatorId._id,
            email: g.creatorId.email,
            nickname: g.creatorId.nickname,
          }
        : null,
    })),
  });
});

r.post('/groups/:groupId/approve', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const g = await Group.findById(req.params.groupId);
  if (!g) return res.status(404).json({ error: 'Group not found' });
  if (g.status === 'approved') return res.json({ ok: true, alreadyApproved: true });
  g.status = 'approved';
  g.approvedAt = new Date();
  g.approvedByUserId = req.user.id;
  await g.save();
  return res.json({ ok: true });
});

r.post('/groups/:groupId/reject', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  /** Rejection deletes the pending group entirely — no orphaned data to clean up since nothing
   * else can attach to a pending group. */
  const g = await Group.findById(req.params.groupId);
  if (!g) return res.status(404).json({ error: 'Group not found' });
  if (g.status !== 'pending') {
    return res.status(400).json({ error: 'Only pending groups can be rejected' });
  }
  await Group.deleteOne({ _id: g._id });
  return res.json({ ok: true });
});

/** Transfer ownership. The new owner must already be a group member; old owner is demoted to ops. */
r.post(
  '/groups/:groupId/transfer-ownership',
  param('groupId').isMongoId(),
  body('newOwnerId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    const newOwnerId = String(req.body.newOwnerId);
    if (String(g.creatorId) === newOwnerId) {
      return res.status(400).json({ error: 'User is already the owner' });
    }
    const newOwnerMember = (g.members || []).find((m) => String(m.userId) === newOwnerId);
    if (!newOwnerMember) {
      return res.status(400).json({ error: 'New owner must already be a group member' });
    }
    const oldOwnerId = String(g.creatorId);

    /** Old owner: demote to ops with empty watches. Insert if missing (creator wasn't in members[]
     * before this PR). */
    const oldOwnerMember = (g.members || []).find((m) => String(m.userId) === oldOwnerId);
    if (oldOwnerMember) {
      oldOwnerMember.roles = ['ops'];
      oldOwnerMember.watches = [];
    } else {
      g.members.push({
        userId: g.creatorId,
        roles: ['ops'],
        watches: [],
        joinedAt: g.createdAt || new Date(),
      });
    }
    g.creatorId = new mongoose.Types.ObjectId(newOwnerId);
    /** Clear removal-assister state — new owner sets their own. */
    g.removalAssisterId = null;
    g.removalOwnerConfirmedAt = null;
    g.removalAssisterConfirmedAt = null;
    await g.save();
    return res.json({ ok: true });
  }
);

/**
 * Cross-group total storage usage. Returns per-collection bytes + counts, summed across every
 * group. Same `$bsonSize over $$ROOT` approach as per-group storage; this one isn't scoped.
 * Don't poll — it scans the full dataset.
 */
r.get('/storage', async (_req, res) => {
  async function totalSize(Model) {
    const out = await Model.aggregate([
      { $group: { _id: null, bytes: { $sum: { $bsonSize: '$$ROOT' } }, count: { $sum: 1 } } },
    ]);
    return { bytes: out[0]?.bytes ?? 0, count: out[0]?.count ?? 0 };
  }

  const [
    users,
    groups,
    bids,
    links,
    interviews,
    activity,
    joinReqs,
    badgeReqs,
    feedbacks,
  ] = await Promise.all([
    totalSize(User),
    totalSize(Group),
    totalSize(UserBid),
    totalSize(GroupLink),
    totalSize(Interview),
    totalSize(BidAssistantActivity),
    totalSize(JoinRequest),
    totalSize(ProfileBadgeRequest),
    totalSize(Feedback),
  ]);

  const collections = [
    { name: 'users', ...users },
    { name: 'groups', ...groups },
    { name: 'userbids', ...bids },
    { name: 'grouplinks', ...links },
    { name: 'interviews', ...interviews },
    { name: 'bidassistantactivities', ...activity },
    { name: 'joinrequests', ...joinReqs },
    { name: 'profilebadgerequests', ...badgeReqs },
    { name: 'feedbacks', ...feedbacks },
  ];
  const totalBytes = collections.reduce((s, c) => s + c.bytes, 0);
  const totalCount = collections.reduce((s, c) => s + c.count, 0);
  const groupCount = await Group.countDocuments({});
  return res.json({ collections, totalBytes, totalCount, groupCount });
});

export default r;
