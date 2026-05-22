import { Router } from 'express';
import bcrypt from 'bcryptjs';
import { body, param, query, validationResult } from 'express-validator';
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

    /**
     * Hand the admin (creatorId) to the new owner. Old owner keeps their existing member roles
     * (they aren't demoted). Old owner is ensured to be in `members[]` with their previous roles
     * if they weren't tracked there (legacy data); default to ['ops'] only if they were brand new.
     */
    const oldOwnerMember = (g.members || []).find((m) => String(m.userId) === oldOwnerId);
    if (!oldOwnerMember) {
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

/**
 * List users for the admin password-reset UI. Supports a simple `search` substring against email
 * and nickname. Capped at 200 results to keep payloads small; admin can refine via search.
 */
r.get(
  '/users',
  query('search').optional().isString().isLength({ max: 200 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const s = String(req.query.search || '').trim();
    const filter = {};
    if (s) {
      const re = new RegExp(s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i');
      filter.$or = [{ email: re }, { nickname: re }];
    }
    const users = await User.find(filter)
      .select('email nickname platformRole createdAt')
      .sort({ createdAt: -1 })
      .limit(200)
      .lean();
    return res.json({
      users: users.map((u) => ({
        id: String(u._id),
        email: u.email,
        nickname: u.nickname,
        platformRole: u.platformRole === 'admin' ? 'admin' : 'user',
        createdAt: u.createdAt,
      })),
    });
  }
);

/**
 * Reset any user's password. Replaces the bcrypt hash with one for the new plaintext. The platform
 * admin can reset their own password too (e.g. after the seeded default is rotated). 8-128 char
 * range matches the registration validator.
 */
r.post(
  '/users/:userId/reset-password',
  param('userId').isMongoId(),
  body('newPassword').isString().isLength({ min: 8, max: 128 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const u = await User.findById(req.params.userId);
    if (!u) return res.status(404).json({ error: 'User not found' });
    u.passwordHash = await bcrypt.hash(req.body.newPassword, 12);
    await u.save();
    return res.json({ ok: true });
  }
);

export default r;
