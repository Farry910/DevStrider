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
import { assertGroupCreator, assertGroupMember, assertPlatformAdmin } from '../services/membership.js';
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
import { Notification } from '../models/Notification.js';
import { emitNotificationToUser } from '../socket/hexGameSocket.js';
import { getOrSeedGroupProfile, patchGroupProfile } from '../services/groupProfileService.js';

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
  /** Discovery view: hide pending-approval groups so users can't join them yet. */
  const groups = await Group.find({ status: { $ne: 'pending' } })
    .sort({ name: 1 })
    .lean();
  return res.json({ groups });
});

/**
 * Create a group. New groups start `status: 'pending'` and are invisible in normal listings until
 * a platform admin approves them. The creator is auto-added as the only member (admin via creatorId).
 */
r.post(
  '/',
  body('name').trim().isLength({ min: 1, max: 120 }),
  body('locationKey').trim().isLength({ min: 1, max: 64 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const { name, locationKey } = req.body;
    const g = new Group({
      name,
      locationKey: String(locationKey).toLowerCase(),
      creatorId: req.user.id,
      memberIds: [req.user.id],
      members: [{ userId: req.user.id, roles: ['ops'], watches: [], joinedAt: new Date() }],
      status: 'pending',
    });
    await g.save();
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
    /** Add joiner with default role ['ops'] and empty watches; admin can grant more later. */
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    const uid = jr.userId;
    const alreadyMember = (g.members || []).some((m) => String(m.userId) === String(uid));
    if (!alreadyMember) {
      g.members.push({ userId: uid, roles: ['ops'], watches: [], joinedAt: new Date() });
      await g.save();
    }
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

/**
 * Member requests changes to their own roles. Creates a notification for the group owner so they
 * can review and apply the change in the Members & roles panel. No state change is made until the
 * owner explicitly grants the roles.
 */
r.post(
  '/:groupId/role-requests',
  param('groupId').isMongoId(),
  body('requestedRoles').isArray({ min: 1, max: 3 }),
  body('requestedRoles.*').isIn(['bidder', 'caller', 'ops']),
  body('message').optional().isString().isLength({ max: 500 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const requestedRoles = [...new Set(req.body.requestedRoles)];
    const message = req.body.message ? String(req.body.message).trim() : '';
    const requester = await User.findById(req.user.id).select('nickname email').lean();
    const notif = await Notification.create({
      userId: m.group.creatorId,
      kind: 'role_request',
      payload: {
        groupId: String(m.group._id),
        groupName: m.group.name,
        requesterId: String(req.user.id),
        requesterNickname: requester?.nickname || '',
        requesterEmail: requester?.email || '',
        requestedRoles,
        message,
      },
    });
    emitNotificationToUser(String(m.group.creatorId), {
      id: String(notif._id),
      kind: 'role_request',
      payload: notif.payload,
      createdAt: notif.createdAt,
    });
    return res.status(201).json({ ok: true });
  }
);

/**
 * Group owner: approve a pending role-request notification. Applies the requested roles to the
 * requester's member record and marks the notification read. The requester's existing roles are
 * replaced with the requested set (deduped, defaulting to ['ops'] if empty).
 */
r.post(
  '/:groupId/role-requests/:notificationId/approve',
  param('groupId').isMongoId(),
  param('notificationId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const notif = await Notification.findOne({
      _id: req.params.notificationId,
      userId: req.user.id,
      kind: 'role_request',
    });
    if (!notif) return res.status(404).json({ error: 'Role request not found' });
    const payload = notif.payload || {};
    if (String(payload.groupId) !== String(req.params.groupId)) {
      return res.status(400).json({ error: 'Role request does not match this group' });
    }
    const requesterId = String(payload.requesterId || '');
    if (!mongoose.isValidObjectId(requesterId)) {
      return res.status(400).json({ error: 'Invalid requester id in notification' });
    }
    const requestedRoles = Array.isArray(payload.requestedRoles)
      ? [...new Set(payload.requestedRoles.filter((r) => ['bidder', 'caller', 'ops'].includes(r)))]
      : [];
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    let target = g.members.find((m) => String(m.userId) === requesterId);
    if (!target && String(g.creatorId) === requesterId) {
      g.members.push({
        userId: g.creatorId,
        roles: ['ops'],
        watches: [],
        joinedAt: g.createdAt || new Date(),
      });
      target = g.members[g.members.length - 1];
    }
    if (!target) return res.status(404).json({ error: 'Requester is no longer a member' });
    target.roles = requestedRoles.length === 0 ? ['ops'] : requestedRoles;
    await g.save();
    if (!notif.readAt) {
      notif.readAt = new Date();
      await notif.save();
    }
    return res.json({ ok: true, roles: target.roles, userId: requesterId });
  }
);

/** Group owner: deny a pending role-request notification — marks read, no role change. */
r.post(
  '/:groupId/role-requests/:notificationId/deny',
  param('groupId').isMongoId(),
  param('notificationId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const notif = await Notification.findOne({
      _id: req.params.notificationId,
      userId: req.user.id,
      kind: 'role_request',
    });
    if (!notif) return res.status(404).json({ error: 'Role request not found' });
    if (String((notif.payload || {}).groupId) !== String(req.params.groupId)) {
      return res.status(400).json({ error: 'Role request does not match this group' });
    }
    if (!notif.readAt) {
      notif.readAt = new Date();
      await notif.save();
    }
    return res.json({ ok: true });
  }
);

/**
 * Per-group profile for the current user. Lazily seeded from the user's top-level profile on
 * first read so existing users don't lose data; subsequent edits are group-scoped. Only members
 * of the group can read/write their own per-group profile.
 */
r.get('/:groupId/profile/me', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const m = await assertGroupMember(req.user.id, req.params.groupId);
  if (!m.ok) return res.status(m.status).json({ error: m.error });
  const doc = await getOrSeedGroupProfile(req.user.id, req.params.groupId);
  return res.json({ profile: doc.toObject() });
});

const isOptionalYear = (v) =>
  v === null || v === undefined || v === '' || (Number.isInteger(Number(v)) && Number(v) >= 1900 && Number(v) <= 2100);

r.patch(
  '/:groupId/profile/me',
  param('groupId').isMongoId(),
  body('displayName').optional().isString().isLength({ max: 120 }),
  body('headline').optional().isString().isLength({ max: 200 }),
  body('location').optional().isString().isLength({ max: 120 }),
  body('phone').optional().isString().isLength({ max: 40 }),
  body('personalEmail').optional().isString().isLength({ max: 200 }),
  body('linkedinUrl').optional().isString().isLength({ max: 300 }),
  body('education').optional().isArray({ max: 20 }),
  body('education.*.degree').optional().isString().isLength({ max: 120 }),
  body('education.*.school').optional().isString().isLength({ max: 200 }),
  body('education.*.location').optional().isString().isLength({ max: 120 }),
  body('education.*.startYear').optional({ nullable: true }).custom(isOptionalYear),
  body('education.*.endYear').optional({ nullable: true }).custom(isOptionalYear),
  body('certifications').optional().isArray({ max: 30 }),
  body('certifications.*.name').optional().isString().isLength({ max: 200 }),
  body('certifications.*.issuer').optional().isString().isLength({ max: 200 }),
  body('certifications.*.year').optional({ nullable: true }).custom(isOptionalYear),
  body('experiences').optional().isArray({ max: 30 }),
  body('experiences.*.company').optional().isString().isLength({ max: 200 }),
  body('experiences.*.role').optional().isString().isLength({ max: 200 }),
  body('experiences.*.location').optional().isString().isLength({ max: 120 }),
  body('experiences.*.startYear').optional({ nullable: true }).custom(isOptionalYear),
  body('experiences.*.endYear').optional({ nullable: true }).custom(isOptionalYear),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const doc = await patchGroupProfile(req.user.id, req.params.groupId, req.body || {});
    return res.json({ profile: doc.toObject() });
  }
);

/** Group owner: toggle past-day bid edits (add link / edit / delete / fast-feed on non-today boards). */
r.patch(
  '/:groupId/allow-past-day-edit',
  param('groupId').isMongoId(),
  body('allowPastDayEdit').isBoolean(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    g.allowPastDayEdit = Boolean(req.body.allowPastDayEdit);
    await g.save();
    return res.json({ allowPastDayEdit: g.allowPastDayEdit });
  }
);

r.get('/:groupId/me', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const g = await Group.findById(req.params.groupId).lean();
  if (!g) return res.status(404).json({ error: 'Group not found' });
  const uid = String(req.user.id);
  const isMember = (g.memberIds || []).some((id) => String(id) === uid);
  const isCreator = String(g.creatorId) === uid;
  const memberRecord = (g.members || []).find((m) => String(m.userId) === uid) || null;
  const memberRoles = memberRecord ? memberRecord.roles : [];
  const watches = memberRecord ? (memberRecord.watches || []).map(String) : [];
  /** Effective roles include 'admin' for the creator; explicit member roles otherwise. */
  const effectiveRoles = isCreator
    ? ['admin', 'bidder', 'caller', 'ops']
    : memberRoles;
  const assisterId = g.removalAssisterId ? String(g.removalAssisterId) : null;
  const timers = mergeGroupTimers(g.timers, {});
  return res.json({
    group: { ...g, timers },
    role: isCreator ? 'creator' : isMember ? 'member' : 'none',
    isMember,
    /** New role surface for UI gating. */
    effectiveRoles,
    memberRoles,
    watches,
    status: g.status || 'approved',
    allowPastDayEdit: Boolean(g.allowPastDayEdit),
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
 * Group members + their roles + watches. Visible to any member (so callers/ops know who's who).
 * Admins use this list to manage role grants.
 */
r.get('/:groupId/members-detailed', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  /** Platform admin can read any group's members (needed for ownership-transfer UI). */
  const platform = await assertPlatformAdmin(req.user.id);
  let g;
  if (platform.ok) {
    g = await Group.findById(req.params.groupId).lean();
    if (!g) return res.status(404).json({ error: 'Group not found' });
  } else {
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    g = m.group;
  }
  const userIds = (g.members || []).map((mm) => mm.userId);
  const users = await User.find({ _id: { $in: userIds } })
    .select('nickname email avatarId')
    .lean();
  const userById = new Map(users.map((u) => [String(u._id), u]));
  const members = (g.members || []).map((mm) => {
    const u = userById.get(String(mm.userId));
    return {
      userId: String(mm.userId),
      nickname: u?.nickname || '',
      email: u?.email || '',
      avatarId: u?.avatarId || 'initial',
      roles: mm.roles || [],
      watches: (mm.watches || []).map(String),
      joinedAt: mm.joinedAt,
      isCreator: String(g.creatorId) === String(mm.userId),
    };
  });
  return res.json({ members });
});

/** Admin: replace a member's roles. Sends 400 if the target is the creator (admin is implicit). */
r.patch(
  '/:groupId/members/:userId/roles',
  param('groupId').isMongoId(),
  param('userId').isMongoId(),
  body('roles').isArray({ min: 0, max: 3 }),
  body('roles.*').isIn(['bidder', 'caller', 'ops']),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    /**
     * The creator is implicit admin via creatorId; their explicit member roles can still be set
     * (bidder/caller/ops) so they show up in caller watch picks and bidder counts. If they aren't
     * in members[] yet (legacy data), insert with whatever roles are being assigned.
     */
    let target = g.members.find((m) => String(m.userId) === String(req.params.userId));
    if (!target && String(g.creatorId) === String(req.params.userId)) {
      g.members.push({
        userId: g.creatorId,
        roles: ['ops'],
        watches: [],
        joinedAt: g.createdAt || new Date(),
      });
      target = g.members[g.members.length - 1];
    }
    if (!target) return res.status(404).json({ error: 'Member not found' });
    /** Dedup + ensure default ['ops'] if empty so the user always has at least watch capability. */
    const next = [...new Set(req.body.roles)];
    target.roles = next.length === 0 ? ['ops'] : next;
    await g.save();
    return res.json({ ok: true, roles: target.roles });
  }
);

/** Admin: replace a member's watches (the set of bidder userIds they can see). */
r.patch(
  '/:groupId/members/:userId/watches',
  param('groupId').isMongoId(),
  param('userId').isMongoId(),
  body('watches').isArray({ min: 0, max: 500 }),
  body('watches.*').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const g = await Group.findById(req.params.groupId);
    if (!g) return res.status(404).json({ error: 'Group not found' });
    const target = g.members.find((m) => String(m.userId) === String(req.params.userId));
    if (!target) return res.status(404).json({ error: 'Member not found' });
    /** Only allow watching actual group members. */
    const memberSet = new Set(g.members.map((m) => String(m.userId)));
    const watches = [...new Set(req.body.watches.map(String))].filter((id) => memberSet.has(id));
    target.watches = watches;
    await g.save();
    return res.json({ ok: true, watches: target.watches });
  }
);

/**
 * Per-collection bytes/count for this group's data. Restricted to the group creator.
 * Uses $bsonSize over $$ROOT — accurate but scans documents, so don't poll this on a hot path.
 */
r.get('/:groupId/storage', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const cr = await assertGroupCreator(req.user.id, req.params.groupId);
  if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
  const gid = new mongoose.Types.ObjectId(req.params.groupId);

  async function sizeOf(Model) {
    const out = await Model.aggregate([
      { $match: { groupId: gid } },
      { $group: { _id: null, bytes: { $sum: { $bsonSize: '$$ROOT' } }, count: { $sum: 1 } } },
    ]);
    return { bytes: out[0]?.bytes ?? 0, count: out[0]?.count ?? 0 };
  }

  const [bids, links, interviews, activity, joinReqs, badgeReqs, fb] = await Promise.all([
    sizeOf(UserBid),
    sizeOf(GroupLink),
    sizeOf(Interview),
    sizeOf(BidAssistantActivity),
    sizeOf(JoinRequest),
    sizeOf(ProfileBadgeRequest),
    sizeOf(Feedback),
  ]);

  const collections = [
    { name: 'userbids', ...bids },
    { name: 'grouplinks', ...links },
    { name: 'interviews', ...interviews },
    { name: 'bidassistantactivities', ...activity },
    { name: 'joinrequests', ...joinReqs },
    { name: 'profilebadgerequests', ...badgeReqs },
    { name: 'feedbacks', ...fb },
  ];
  const totalBytes = collections.reduce((s, c) => s + c.bytes, 0);
  const totalCount = collections.reduce((s, c) => s + c.count, 0);
  return res.json({ collections, totalBytes, totalCount });
});

/**
 * Prune old per-group data. Restricted to the group creator.
 * - olderThanDays (7-3650): cutoff = now - olderThanDays
 * - dryRun (default false): return counts only
 * Deletes:
 *   - UserBid (firstCreatedAt < cutoff)
 *   - Interview (createdAt < cutoff)
 *   - BidAssistantActivity (createdAt < cutoff)
 *   - GroupLink only if old AND has no remaining UserBids
 */
r.post(
  '/:groupId/prune',
  param('groupId').isMongoId(),
  body('olderThanDays').isInt({ min: 7, max: 3650 }),
  body('dryRun').optional().isBoolean(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const cr = await assertGroupCreator(req.user.id, req.params.groupId);
    if (!cr.ok) return res.status(cr.status).json({ error: cr.error });
    const gid = new mongoose.Types.ObjectId(req.params.groupId);
    const olderThanDays = Number(req.body.olderThanDays);
    const dryRun = req.body.dryRun === true;
    const cutoff = new Date(Date.now() - olderThanDays * 86400000);

    if (dryRun) {
      const [bidCount, ivCount, actCount, bidLinkAgg, oldLinks] = await Promise.all([
        UserBid.countDocuments({ groupId: gid, firstCreatedAt: { $lt: cutoff } }),
        Interview.countDocuments({ groupId: gid, createdAt: { $lt: cutoff } }),
        BidAssistantActivity.countDocuments({ groupId: gid, createdAt: { $lt: cutoff } }),
        UserBid.aggregate([
          { $match: { groupId: gid } },
          {
            $group: {
              _id: '$groupLinkId',
              kept: { $sum: { $cond: [{ $gte: ['$firstCreatedAt', cutoff] }, 1, 0] } },
            },
          },
        ]),
        GroupLink.find({ groupId: gid, createdAt: { $lt: cutoff } }).select('_id').lean(),
      ]);
      const keptByLink = new Map(bidLinkAgg.map((r) => [String(r._id), r.kept]));
      const linkCount = oldLinks.filter((l) => (keptByLink.get(String(l._id)) ?? 0) === 0).length;
      return res.json({
        dryRun: true,
        cutoff: cutoff.toISOString(),
        olderThanDays,
        wouldDelete: {
          userbids: bidCount,
          interviews: ivCount,
          bidassistantactivities: actCount,
          grouplinks: linkCount,
        },
      });
    }

    const [bidDel, ivDel, actDel] = await Promise.all([
      UserBid.deleteMany({ groupId: gid, firstCreatedAt: { $lt: cutoff } }),
      Interview.deleteMany({ groupId: gid, createdAt: { $lt: cutoff } }),
      BidAssistantActivity.deleteMany({ groupId: gid, createdAt: { $lt: cutoff } }),
    ]);

    let linkDelCount = 0;
    const oldLinks = await GroupLink.find({ groupId: gid, createdAt: { $lt: cutoff } })
      .select('_id')
      .lean();
    const oldLinkIds = oldLinks.map((l) => l._id);
    if (oldLinkIds.length > 0) {
      const remaining = await UserBid.aggregate([
        { $match: { groupId: gid, groupLinkId: { $in: oldLinkIds } } },
        { $group: { _id: '$groupLinkId' } },
      ]);
      const aliveIds = new Set(remaining.map((r) => String(r._id)));
      const toDelete = oldLinkIds.filter((id) => !aliveIds.has(String(id)));
      if (toDelete.length > 0) {
        const out = await GroupLink.deleteMany({ _id: { $in: toDelete } });
        linkDelCount = out.deletedCount ?? 0;
      }
    }

    return res.json({
      dryRun: false,
      cutoff: cutoff.toISOString(),
      olderThanDays,
      deleted: {
        userbids: bidDel.deletedCount ?? 0,
        interviews: ivDel.deletedCount ?? 0,
        bidassistantactivities: actDel.deletedCount ?? 0,
        grouplinks: linkDelCount,
      },
    });
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
