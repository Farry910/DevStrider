import mongoose from 'mongoose';
import { User } from '../models/User.js';
import { ProfileBadgeRequest } from '../models/ProfileBadgeRequest.js';
import { PROFILE_BADGE_KEYS, sanitizeProfileBadgeKeys } from '../constants/profileBadgeTypes.js';
import { assertGroupMember, assertGroupCreator } from './membership.js';

function toOid(id) {
  return id instanceof mongoose.Types.ObjectId ? id : new mongoose.Types.ObjectId(id);
}

function userHasGrantInGroup(user, groupId, badgeKey) {
  const grants = user?.profileBadgeGrants;
  if (!grants || !Array.isArray(grants)) return false;
  const g = String(groupId);
  return grants.some((x) => x && String(x.groupId) === g && x.badgeKey === badgeKey);
}

/**
 * @param {string} userId
 * @param {string} groupId
 * @param {string} badgeKey
 */
export async function createProfileBadgeRequest(userId, groupId, badgeKey) {
  if (!PROFILE_BADGE_KEYS.has(badgeKey)) {
    return { ok: false, status: 400, error: 'Unknown badge type' };
  }
  const m = await assertGroupMember(userId, groupId);
  if (!m.ok) return m;
  const uid = toOid(userId);
  const gid = toOid(groupId);
  const user = await User.findById(uid).select('profileBadgeGrants').lean();
  if (!user) return { ok: false, status: 404, error: 'User not found' };
  if (userHasGrantInGroup(user, groupId, badgeKey)) {
    return { ok: false, status: 400, error: 'You already have this badge in this group' };
  }
  const pending = await ProfileBadgeRequest.findOne({
    userId: uid,
    groupId: gid,
    badgeKey,
    status: 'pending',
  }).lean();
  if (pending) {
    return { ok: false, status: 409, error: 'A request for this badge is already pending' };
  }
  const doc = await ProfileBadgeRequest.create({
    userId: uid,
    groupId: gid,
    badgeKey,
    status: 'pending',
  });
  return { ok: true, request: doc.toObject() };
}

/** @param {string} groupId */
export async function listPendingProfileBadgeRequestsForGroup(groupId) {
  const gid = toOid(groupId);
  return ProfileBadgeRequest.find({ groupId: gid, status: 'pending' })
    .sort({ createdAt: 1 })
    .populate('userId', 'email nickname')
    .lean();
}

/**
 * @param {string} requestId
 * @param {string} reviewerUserId
 * @param {string} groupId
 */
export async function approveProfileBadgeRequest(requestId, reviewerUserId, groupId) {
  const cr = await assertGroupCreator(reviewerUserId, groupId);
  if (!cr.ok) return cr;
  const rid = toOid(requestId);
  const gid = toOid(groupId);
  const reqDoc = await ProfileBadgeRequest.findById(rid);
  if (!reqDoc) return { ok: false, status: 404, error: 'Request not found' };
  if (String(reqDoc.groupId) !== String(gid)) {
    return { ok: false, status: 400, error: 'Request does not belong to this group' };
  }
  if (reqDoc.status !== 'pending') {
    return { ok: false, status: 400, error: 'Request is not pending' };
  }
  if (!PROFILE_BADGE_KEYS.has(reqDoc.badgeKey)) {
    return { ok: false, status: 400, error: 'Invalid badge key on request' };
  }
  reqDoc.status = 'approved';
  reqDoc.reviewedByUserId = toOid(reviewerUserId);
  reqDoc.reviewedAt = new Date();
  await reqDoc.save();
  await User.findByIdAndUpdate(reqDoc.userId, {
    $addToSet: {
      profileBadgeGrants: { groupId: gid, badgeKey: reqDoc.badgeKey },
    },
  });
  return { ok: true, request: reqDoc.toObject() };
}

/**
 * @param {string} requestId
 * @param {string} reviewerUserId
 * @param {string} groupId
 * @param {string} [note]
 */
export async function rejectProfileBadgeRequest(requestId, reviewerUserId, groupId, note = '') {
  const cr = await assertGroupCreator(reviewerUserId, groupId);
  if (!cr.ok) return cr;
  const rid = toOid(requestId);
  const gid = toOid(groupId);
  const reqDoc = await ProfileBadgeRequest.findById(rid);
  if (!reqDoc) return { ok: false, status: 404, error: 'Request not found' };
  if (String(reqDoc.groupId) !== String(gid)) {
    return { ok: false, status: 400, error: 'Request does not belong to this group' };
  }
  if (reqDoc.status !== 'pending') {
    return { ok: false, status: 400, error: 'Request is not pending' };
  }
  reqDoc.status = 'rejected';
  reqDoc.reviewedByUserId = toOid(reviewerUserId);
  reqDoc.reviewedAt = new Date();
  reqDoc.reviewNote = String(note || '').slice(0, 500);
  await reqDoc.save();
  return { ok: true, request: reqDoc.toObject() };
}

/** @param {string} userId @param {string} groupId */
export async function listRequestsForUserInGroup(userId, groupId) {
  const uid = toOid(userId);
  const gid = toOid(groupId);
  return ProfileBadgeRequest.find({ userId: uid, groupId: gid })
    .sort({ updatedAt: -1 })
    .limit(200)
    .lean();
}

/**
 * Approved keys in this group + request history for the current member.
 * @param {string} userId
 * @param {string} groupId
 */
export async function getProfileBadgeSummaryForUserInGroup(userId, groupId) {
  const m = await assertGroupMember(userId, groupId);
  if (!m.ok) return m;
  const uid = toOid(userId);
  const gid = toOid(groupId);
  const gStr = String(gid);
  const user = await User.findById(uid).select('profileBadgeGrants').lean();
  if (!user) return { ok: false, status: 404, error: 'User not found' };
  const grants = user.profileBadgeGrants || [];
  const approvedKeys = sanitizeProfileBadgeKeys(
    grants.filter((x) => x && String(x.groupId) === gStr).map((x) => x.badgeKey)
  );
  const requests = await listRequestsForUserInGroup(userId, groupId);
  return { ok: true, approvedKeys, requests };
}
