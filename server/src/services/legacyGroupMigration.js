import { Group } from '../models/Group.js';

/**
 * Backfill `members[]` from legacy `memberIds[]` and stamp `status='approved'` for groups that
 * pre-date the role schema. Runs on every boot but is idempotent — any group that already has a
 * non-empty `members[]` and a `status` field is skipped, so subsequent boots no-op cheaply.
 *
 * Each migrated member starts with `roles: ['ops']` (read-only on watched users). The group
 * creator stays the implicit ADMIN via `creatorId` regardless of their member-row role.
 */
export async function migrateLegacyGroups() {
  const cursor = Group.find({
    $or: [
      { members: { $exists: false } },
      { members: { $size: 0 } },
      { status: { $exists: false } },
    ],
  }).cursor();

  let migrated = 0;
  for await (const g of cursor) {
    let touched = false;
    if (!Array.isArray(g.members) || g.members.length === 0) {
      g.members = (g.memberIds || []).map((uid) => ({
        userId: uid,
        roles: ['ops'],
        watches: [],
        joinedAt: g.createdAt || new Date(),
      }));
      touched = true;
    }
    if (!g.status) {
      g.status = 'approved';
      touched = true;
    }
    if (touched) {
      await g.save();
      migrated += 1;
    }
  }
  if (migrated > 0) {
    console.log(`[legacyGroupMigration] Migrated ${migrated} group(s) to new schema`);
  }
}
