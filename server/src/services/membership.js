import { Group } from '../models/Group.js';

export async function assertGroupMember(userId, groupId) {
  const g = await Group.findById(groupId).lean();
  if (!g) return { ok: false, status: 404, error: 'Group not found' };
  const uid = String(userId);
  const isMember = g.memberIds.some((id) => String(id) === uid);
  if (!isMember) return { ok: false, status: 403, error: 'Not a member of this group' };
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
