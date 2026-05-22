import mongoose from 'mongoose';
import { GroupProfile } from '../models/GroupProfile.js';
import { User } from '../models/User.js';

/**
 * Returns the GroupProfile for (userId, groupId), creating it on first read by snapshotting the
 * user's current resume-header fields. Existing groups don't lose data; subsequent edits are
 * group-scoped. Concurrent first-reads are handled by the unique (userId, groupId) index — the
 * duplicate-key throw falls back to a fetch.
 */
export async function getOrSeedGroupProfile(userId, groupId) {
  const uid = new mongoose.Types.ObjectId(String(userId));
  const gid = new mongoose.Types.ObjectId(String(groupId));
  const existing = await GroupProfile.findOne({ userId: uid, groupId: gid });
  if (existing) return existing;
  const u = await User.findById(uid)
    .select(
      'displayName headline location phone personalEmail linkedinUrl education certifications'
    )
    .lean();
  try {
    return await GroupProfile.create({
      userId: uid,
      groupId: gid,
      displayName: u?.displayName || '',
      headline: u?.headline || '',
      location: u?.location || '',
      phone: u?.phone || '',
      personalEmail: u?.personalEmail || '',
      linkedinUrl: u?.linkedinUrl || '',
      education: Array.isArray(u?.education) ? u.education : [],
      certifications: Array.isArray(u?.certifications) ? u.certifications : [],
      experiences: [],
    });
  } catch (e) {
    if (e && e.code === 11000) {
      return GroupProfile.findOne({ userId: uid, groupId: gid });
    }
    throw e;
  }
}

/**
 * Batch-load profiles for a set of users in one group. Skips seeding — used by the bid board so
 * it doesn't bulk-create profiles for users who haven't visited yet (those rows fall back to the
 * empty defaults). Returns a Map keyed by stringified userId.
 */
export async function loadGroupProfilesForUsers(groupId, userIds) {
  if (!userIds || userIds.length === 0) return new Map();
  const gid = new mongoose.Types.ObjectId(String(groupId));
  const uids = [...new Set(userIds.map((id) => String(id)))]
    .filter((s) => mongoose.isValidObjectId(s))
    .map((s) => new mongoose.Types.ObjectId(s));
  const rows = await GroupProfile.find({ groupId: gid, userId: { $in: uids } }).lean();
  const out = new Map();
  for (const p of rows) out.set(String(p.userId), p);
  return out;
}

const ALLOWED_FIELDS = new Set([
  'displayName',
  'headline',
  'location',
  'phone',
  'personalEmail',
  'linkedinUrl',
  'education',
  'certifications',
  'experiences',
]);

/**
 * Apply a partial update to (userId, groupId). Only whitelisted fields are honored; unknown keys
 * are silently dropped. Returns the saved doc.
 */
export async function patchGroupProfile(userId, groupId, patch) {
  const doc = await getOrSeedGroupProfile(userId, groupId);
  for (const key of Object.keys(patch || {})) {
    if (!ALLOWED_FIELDS.has(key)) continue;
    doc[key] = patch[key];
  }
  await doc.save();
  return doc;
}
