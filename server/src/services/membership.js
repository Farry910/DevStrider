import { Group } from '../models/Group.js';
import { User } from '../models/User.js';

/** Caller is in members[] OR is the creator (admin). */
export async function assertGroupMember(userId, groupId) {
  const g = await Group.findById(groupId).lean();
  if (!g) return { ok: false, status: 404, error: 'Group not found' };
  const uid = String(userId);
  const isMember = (g.memberIds || []).some((id) => String(id) === uid);
  if (!isMember && String(g.creatorId) !== uid) {
    return { ok: false, status: 403, error: 'Not a member of this group' };
  }
  return { ok: true, group: g };
}

export async function assertGroupCreator(userId, groupId) {
  const g = await Group.findById(groupId).lean();
  if (!g) return { ok: false, status: 404, error: 'Group not found' };
  if (String(g.creatorId) !== String(userId)) {
    return { ok: false, status: 403, error: 'Only the group creator can do this' };
  }
  return { ok: true, group: g };
}

/** Convenience: pull this user's per-group member record. Returns null when not a member. */
export function getMemberRecord(group, userId) {
  if (!group || !Array.isArray(group.members)) return null;
  const uid = String(userId);
  return group.members.find((m) => String(m.userId) === uid) || null;
}

/**
 * The group creator is always treated as having every role implicitly — they don't appear in
 * `members[]` as bidder/caller, they're the ADMIN. Pass `includeAdmin: false` to opt out of that
 * implicit grant when you specifically need the underlying member-role assignment.
 */
export function getEffectiveRoles(group, userId, { includeAdmin = true } = {}) {
  if (!group) return [];
  const uid = String(userId);
  const isCreator = String(group.creatorId) === uid;
  if (isCreator && includeAdmin) return ['admin', 'bidder', 'caller', 'ops'];
  const m = getMemberRecord(group, uid);
  return m ? [...m.roles] : [];
}

/**
 * Gate a write endpoint on the caller having any one of the allowed roles. ADMIN (group creator)
 * implicitly satisfies any role. Returns { ok: true, group } on success.
 *
 * @param {string|ObjectId} userId
 * @param {string|ObjectId} groupId
 * @param {Array<'admin'|'bidder'|'caller'|'ops'>} allowedRoles
 */
export async function assertGroupRole(userId, groupId, allowedRoles) {
  const m = await assertGroupMember(userId, groupId);
  if (!m.ok) return m;
  const roles = getEffectiveRoles(m.group, userId);
  if (!allowedRoles.some((r) => roles.includes(r))) {
    return {
      ok: false,
      status: 403,
      error: `Requires one of: ${allowedRoles.join(', ')}`,
    };
  }
  return { ok: true, group: m.group, roles };
}

export async function assertPlatformAdmin(userId) {
  const u = await User.findById(userId).select('platformRole').lean();
  if (!u || u.platformRole !== 'admin') {
    return { ok: false, status: 403, error: 'Platform admin only' };
  }
  return { ok: true };
}

/**
 * For a CALLER, the set of bidder userIds they're authorized to see. For an OPS, the same set
 * but interpreted as their read-only watchlist. ADMIN sees all members. Empty array = nothing.
 */
export function watchedUserIdsFor(group, userId) {
  const uid = String(userId);
  if (String(group.creatorId) === uid) {
    return (group.memberIds || []).map(String);
  }
  const m = getMemberRecord(group, uid);
  return m ? (m.watches || []).map(String) : [];
}
