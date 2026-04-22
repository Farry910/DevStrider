import { Group } from '../models/Group.js';
import { GroupLink } from '../models/GroupLink.js';
import { UserBid } from '../models/UserBid.js';
import { Interview } from '../models/Interview.js';
import { emitBidBoardInvalidate } from '../socket/hexGameSocket.js';
import { mergeGroupTimers } from '../constants/groupTimers.js';

/** Eligible: marked useless, no group application snapshot, ≤1 bid (creator’s), no interviews on that bid. */
export async function linkEligibleForJunkPurge(link) {
  if (!link.markedUselessAt) return false;
  if (link.appliedAt) return false;
  const bids = await UserBid.find({ groupLinkId: link._id }).select('_id userId').lean();
  if (bids.length > 1) return false;
  if (bids.length === 1) {
    if (String(bids[0].userId) !== String(link.createdByUserId)) return false;
    const iv = await Interview.countDocuments({ bidId: bids[0]._id });
    if (iv > 0) return false;
  }
  return true;
}

/**
 * @param {{ groupId?: string|null, minimumMarkedAgeMs?: number }} opts
 * `minimumMarkedAgeMs`: owner manual purge uses 0; scheduled auto-purge uses per-group grace.
 * @returns {Promise<{ removed: number, linkIds: string[], groupIds: string[] }>}
 */
export async function purgeEligibleJunkLinks({ groupId = null, minimumMarkedAgeMs = 0 } = {}) {
  const and = [{ markedUselessAt: { $ne: null } }];
  if (groupId) and.push({ groupId });
  if (minimumMarkedAgeMs > 0) {
    and.push({
      markedUselessAt: { $lte: new Date(Date.now() - minimumMarkedAgeMs) },
    });
  }

  const candidates = await GroupLink.find({ $and: and }).lean();
  return purgeCandidateLinks(candidates);
}

async function purgeCandidateLinks(candidates) {
  const removedIds = [];
  const groupIdSet = new Set();

  for (const link of candidates) {
    const ok = await linkEligibleForJunkPurge(link);
    if (!ok) continue;
    const bids = await UserBid.find({ groupLinkId: link._id }).select('_id').lean();
    const bidIds = bids.map((b) => b._id);
    if (bidIds.length > 0) {
      await Interview.deleteMany({ bidId: { $in: bidIds } });
      await UserBid.deleteMany({ _id: { $in: bidIds } });
    }
    await GroupLink.deleteOne({ _id: link._id });
    removedIds.push(String(link._id));
    groupIdSet.add(String(link.groupId));
  }

  for (const gid of groupIdSet) {
    emitBidBoardInvalidate(gid);
  }

  return { removed: removedIds.length, linkIds: removedIds, groupIds: [...groupIdSet] };
}

/**
 * Auto-purge: each link must be older than that group’s `junkRemovalGraceMinutes` since marked useless.
 */
export async function purgeEligibleJunkLinksUsingGroupTimers() {
  const candidates = await GroupLink.find({ markedUselessAt: { $ne: null } }).lean();
  if (candidates.length === 0) return { removed: 0, linkIds: [], groupIds: [] };

  const gids = [...new Set(candidates.map((c) => String(c.groupId)))];
  const groups = await Group.find({ _id: { $in: gids } }).select('timers').lean();
  const graceMsByGroup = new Map(
    groups.map((g) => {
      const t = mergeGroupTimers(g.timers, {});
      return [String(g._id), t.junkRemovalGraceMinutes * 60 * 1000];
    })
  );

  const now = Date.now();
  const ready = [];
  for (const link of candidates) {
    const ms =
      graceMsByGroup.get(String(link.groupId)) ??
      mergeGroupTimers(undefined, {}).junkRemovalGraceMinutes * 60 * 1000;
    if (new Date(link.markedUselessAt).getTime() > now - ms) continue;
    ready.push(link);
  }

  return purgeCandidateLinks(ready);
}
