import mongoose from 'mongoose';
import { Group } from '../models/Group.js';
import { GroupLink } from '../models/GroupLink.js';
import { UserBid } from '../models/UserBid.js';
import { User } from '../models/User.js';
import { Interview } from '../models/Interview.js';
import { resolvedTimersFromGroupDoc } from '../constants/groupTimers.js';
import { isAllowedAvatarId } from '../constants/avatarPresets.js';
import { avatarBadgeTintForGroup } from '../constants/profileBadgeTypes.js';
import { norm } from './text.js';
import { escapeRegex } from '../utils/regex.js';

const UB_COL = UserBid.collection.name;

/** @param {string} sortParam e.g. linkCreatedAt:desc */
function parseSort(sortParam) {
  const allowed = new Set([
    'linkCreatedAt',
    'url',
    'resumeId',
    'company',
    'role',
    'status',
    'origin',
    'jobDescription',
    'bidUpdatedAt',
  ]);
  const [field, dirRaw] = String(sortParam || 'linkCreatedAt:desc').split(':');
  const fieldOk = allowed.has(field) ? field : 'linkCreatedAt';
  const dir = dirRaw === 'asc' ? 1 : -1;
  /**
   * Link-column sort groups by emptiness first: asc puts empty bids (no resume/company/role) on top,
   * desc on the bottom. Secondary `createdAt: -1` keeps newest within each group.
   */
  const sortMap = {
    linkCreatedAt: { _emptyRank: -dir, createdAt: -1 },
    url: { _emptyRank: -dir, url: dir },
    resumeId: { 'bid.resumeId': dir },
    company: { 'bid.company': dir },
    role: { 'bid.role': dir },
    status: { 'bid.status': dir },
    origin: { 'bid.origin': dir },
    jobDescription: { 'bid.jobDescription': dir },
    bidUpdatedAt: { 'bid.updatedAt': dir },
  };
  return sortMap[fieldOk];
}

function tstr(v) {
  const s = v == null ? '' : String(v).trim();
  return s.length ? s : '';
}

/** Drop link-only / bid-ready rows (no resume, company, or role). Use for historical day views. */
function excludeLinkOnlyRowsMatch() {
  return {
    $match: {
      $expr: {
        $or: [
          { $gt: [{ $strLenCP: { $trim: { input: { $ifNull: ['$bid.resumeId', ''] } } } }, 0] },
          { $gt: [{ $strLenCP: { $trim: { input: { $ifNull: ['$bid.company', ''] } } } }, 0] },
          { $gt: [{ $strLenCP: { $trim: { input: { $ifNull: ['$bid.role', ''] } } } }, 0] },
        ],
      },
    },
  };
}

function buildFilterStages(filters) {
  const and = [];
  if (tstr(filters?.url)) {
    and.push({ url: { $regex: escapeRegex(tstr(filters.url)), $options: 'i' } });
  }
  if (tstr(filters?.resumeId)) {
    and.push({
      'bid.resumeId': { $regex: escapeRegex(tstr(filters.resumeId)), $options: 'i' },
    });
  }
  if (tstr(filters?.company)) {
    const re = escapeRegex(tstr(filters.company));
    and.push({
      $or: [
        { 'bid.company': { $regex: re, $options: 'i' } },
        { '_peerHint.company': { $regex: re, $options: 'i' } },
      ],
    });
  }
  if (tstr(filters?.role)) {
    const re = escapeRegex(tstr(filters.role));
    and.push({
      $or: [
        { 'bid.role': { $regex: re, $options: 'i' } },
        { '_peerHint.role': { $regex: re, $options: 'i' } },
      ],
    });
  }
  if (tstr(filters?.stacks)) {
    const re = escapeRegex(tstr(filters.stacks));
    and.push({
      $or: [
        { _stacksStr: { $regex: re, $options: 'i' } },
        { _peerStacksStr: { $regex: re, $options: 'i' } },
      ],
    });
  }
  if (tstr(filters?.status)) {
    and.push({
      'bid.status': { $regex: escapeRegex(tstr(filters.status)), $options: 'i' },
    });
  }
  if (tstr(filters?.origin)) {
    and.push({
      'bid.origin': { $regex: escapeRegex(tstr(filters.origin)), $options: 'i' },
    });
  }
  if (tstr(filters?.sharedJd)) {
    and.push({
      sharedJobDescription: { $regex: escapeRegex(tstr(filters.sharedJd)), $options: 'i' },
    });
  }
  if (tstr(filters?.privateJd)) {
    and.push({
      'bid.jobDescription': { $regex: escapeRegex(tstr(filters.privateJd)), $options: 'i' },
    });
  }
  if (tstr(filters?.comment)) {
    and.push({
      'bid.comment': { $regex: escapeRegex(tstr(filters.comment)), $options: 'i' },
    });
  }
  if (and.length === 0) return [];
  return [{ $match: { $and: and } }];
}

const MAX_BID_BOARD_ROWS = 5000;

/**
 * @param {{ groupId: string, userId: string, sort?: string, filters?: object, from: string|Date, to: string|Date, excludeLinkOnly?: boolean }} opts
 * `from` / `to`: ISO bounds [from, to) for **your** bid activity (`bid.updatedAt`), typically local calendar day from the client.
 * `excludeLinkOnly`: omit rows with only a URL / empty bid (no resume, company, or role) — for past-day read-only views.
 */
export async function buildBidBoardPage({
  groupId,
  userId,
  sort,
  filters,
  from,
  to,
  excludeLinkOnly = false,
}) {
  const gid = new mongoose.Types.ObjectId(groupId);
  const uid = new mongoose.Types.ObjectId(userId);
  const sortStage = parseSort(sort);
  const t0 = new Date(from);
  const t1 = new Date(to);

  const groupDoc = await Group.findById(groupId).select('timers').lean();
  const groupTimers = resolvedTimersFromGroupDoc(groupDoc);
  const dupCutoff = new Date(Date.now() - groupTimers.bidDuplicateLookbackDays * 86400000);

  /** Links added this day, or your bid touched this day (shared board + bid-ready rows). */
  const dayMatch = [
    {
      $match: {
        $or: [
          { createdAt: { $gte: t0, $lt: t1 } },
          {
            $and: [{ bid: { $ne: null } }, { 'bid.updatedAt': { $gte: t0, $lt: t1 } }],
          },
        ],
      },
    },
  ];

  const preFacet = [
    { $match: { groupId: gid } },
    {
      $lookup: {
        from: UB_COL,
        let: { linkId: '$_id' },
        pipeline: [
          {
            $match: {
              $expr: {
                $and: [
                  { $eq: ['$groupLinkId', '$$linkId'] },
                  { $eq: ['$userId', uid] },
                ],
              },
            },
          },
        ],
        as: 'bidArr',
      },
    },
    { $addFields: { bid: { $arrayElemAt: ['$bidArr', 0] } } },
    {
      $lookup: {
        from: UB_COL,
        let: { linkId: '$_id' },
        pipeline: [
          {
            $match: {
              $expr: {
                $and: [
                  { $eq: ['$groupLinkId', '$$linkId'] },
                  { $ne: ['$userId', uid] },
                ],
              },
            },
          },
          {
            $match: {
              company: { $exists: true, $nin: [null, ''] },
              role: { $exists: true, $nin: [null, ''] },
            },
          },
          { $sort: { createdAt: 1 } },
          { $limit: 1 },
        ],
        as: 'peerHintArr',
      },
    },
    { $addFields: { _peerHint: { $arrayElemAt: ['$peerHintArr', 0] } } },
    {
      $addFields: {
        _stacksStr: {
          $trim: {
            input: {
              $reduce: {
                input: { $ifNull: ['$bid.primaryStacks', []] },
                initialValue: '',
                in: { $concat: ['$$value', ' ', '$$this'] },
              },
            },
          },
        },
        _peerStacksStr: {
          $trim: {
            input: {
              $reduce: {
                input: { $ifNull: ['$_peerHint.primaryStacks', []] },
                initialValue: '',
                in: { $concat: ['$$value', ' ', '$$this'] },
              },
            },
          },
        },
        /**
         * 1 when this user's bid is missing or has no resume/company/role; 0 otherwise.
         * Powers the Link-column sort (empty rows to top/bottom by direction).
         */
        _emptyRank: {
          $cond: [
            {
              $or: [
                { $eq: [{ $ifNull: ['$bid', null] }, null] },
                {
                  $and: [
                    {
                      $eq: [
                        { $strLenCP: { $trim: { input: { $ifNull: ['$bid.resumeId', ''] } } } },
                        0,
                      ],
                    },
                    {
                      $eq: [
                        { $strLenCP: { $trim: { input: { $ifNull: ['$bid.company', ''] } } } },
                        0,
                      ],
                    },
                    {
                      $eq: [
                        { $strLenCP: { $trim: { input: { $ifNull: ['$bid.role', ''] } } } },
                        0,
                      ],
                    },
                  ],
                },
              ],
            },
            1,
            0,
          ],
        },
      },
    },
    ...dayMatch,
    ...(excludeLinkOnly ? [excludeLinkOnlyRowsMatch()] : []),
    ...buildFilterStages(filters || {}),
    { $unset: ['bidArr', 'peerHintArr'] },
  ];

  const facetStage = {
    $facet: {
      meta: [{ $count: 'n' }],
      rows: [{ $sort: sortStage }, { $limit: MAX_BID_BOARD_ROWS }],
    },
  };

  const agg = await GroupLink.collection
    .aggregate([...preFacet, facetStage], { allowDiskUse: true })
    .toArray();

  const bucket = agg[0] || { meta: [], rows: [] };
  const total = bucket.meta[0]?.n ?? 0;
  const rawRows = bucket.rows || [];

  const creatorIdSet = new Set();
  for (const l of rawRows) {
    if (l.createdByUserId) creatorIdSet.add(String(l.createdByUserId));
  }
  /** @type {Map<string, { nickname: string, avatarId: string, avatarBadge: { color: string, titles: string[] } | null }>} */
  const creatorById = new Map();
  if (creatorIdSet.size > 0) {
    const creatorDocs = await User.find({
      _id: { $in: [...creatorIdSet].map((id) => new mongoose.Types.ObjectId(id)) },
    })
      .select('nickname avatarId profileBadgeGrants')
      .lean();
    for (const u of creatorDocs) {
      creatorById.set(String(u._id), {
        nickname: u.nickname || '',
        avatarId: u.avatarId && isAllowedAvatarId(u.avatarId) ? u.avatarId : 'initial',
        avatarBadge: avatarBadgeTintForGroup(u, gid),
      });
    }
  }

  const linkIds = rawRows.map((l) => l._id);

  const [urlDupCounts, userBidsPop, allUserBidsForDup, allMemberBidsOnPageLinks, interviews] =
    await Promise.all([
      GroupLink.aggregate([
        {
          $match: {
            groupId: gid,
            createdAt: { $gte: dupCutoff },
          },
        },
        { $group: { _id: '$url', count: { $sum: 1 } } },
      ]),
      UserBid.find({ groupId, userId, groupLinkId: { $in: linkIds } })
        .populate('lastModifiedBy', 'nickname email')
        .lean(),
      UserBid.find({ groupId, userId })
        .select('company role groupLinkId createdAt resumeId status')
        .lean(),
      UserBid.find({ groupId, groupLinkId: { $in: linkIds } })
        .select('_id userId groupLinkId status resumeId company role')
        .populate('userId', 'nickname avatarId profileBadgeGrants')
        .lean(),
      Interview.find({
        userId,
        groupId,
        status: { $in: ['scheduled', 'completed', 'passed'] },
      })
        .select('company bidId')
        .lean(),
    ]);

  const bidIdsOnPage = allMemberBidsOnPageLinks.map((b) => b._id);
  const junkIvBlock =
    bidIdsOnPage.length === 0
      ? []
      : await Interview.find({ groupId: gid, bidId: { $in: bidIdsOnPage } })
          .select('bidId')
          .lean();
  const bidIdsWithInterviewForJunk = new Set(junkIvBlock.map((i) => String(i.bidId)));

  /** All links in group + applied snapshot (or fallback from earliest bid with company+role). */
  const allGroupLinks = await GroupLink.find({ groupId: gid })
    .select(
      '_id url createdAt appliedCompany appliedRole appliedAt appliedStacks appliedByUserId'
    )
    .lean();
  const linkDocById = new Map(allGroupLinks.map((l) => [String(l._id), l]));
  const missingAppliedSnap = allGroupLinks.filter(
    (l) => !norm(l.appliedCompany) || !norm(l.appliedRole)
  );
  /** @type {Map<string, { company: string, role: string, at: Date }>} */
  const fallbackCoRoByLink = new Map();
  if (missingAppliedSnap.length > 0) {
    const fbBids = await UserBid.find({
      groupId: gid,
      groupLinkId: { $in: missingAppliedSnap.map((l) => l._id) },
    })
      .select('groupLinkId company role createdAt')
      .sort({ createdAt: 1 })
      .lean();
    for (const b of fbBids) {
      if (!norm(b.company) || !norm(b.role)) continue;
      const lid = String(b.groupLinkId);
      if (!fallbackCoRoByLink.has(lid)) {
        fallbackCoRoByLink.set(lid, { company: b.company, role: b.role, at: b.createdAt });
      }
    }
  }

  function linkEffectiveSnap(linkDoc) {
    if (norm(linkDoc.appliedCompany) && norm(linkDoc.appliedRole)) {
      return {
        company: linkDoc.appliedCompany,
        role: linkDoc.appliedRole,
        sortKey: new Date(linkDoc.appliedAt || linkDoc.createdAt).getTime(),
      };
    }
    const f = fallbackCoRoByLink.get(String(linkDoc._id));
    if (f) {
      return {
        company: f.company,
        role: f.role,
        sortKey: new Date(f.at).getTime(),
      };
    }
    return null;
  }

  const crKey = (c, r) => `${norm(c)}::${norm(r)}`;
  /** @type {Map<string, { linkId: string, sortKey: number }[]>} */
  const linksByCr = new Map();
  for (const l of allGroupLinks) {
    const lc = new Date(l.createdAt).getTime();
    if (lc < dupCutoff.getTime()) continue;
    const s = linkEffectiveSnap(l);
    if (!s) continue;
    const k = crKey(s.company, s.role);
    if (!linksByCr.has(k)) linksByCr.set(k, []);
    linksByCr.get(k).push({ linkId: String(l._id), sortKey: s.sortKey });
  }
  for (const arr of linksByCr.values()) {
    arr.sort((a, b) =>
      a.sortKey !== b.sortKey ? a.sortKey - b.sortKey : a.linkId.localeCompare(b.linkId)
    );
  }

  /** Same normalized company+role on another group link that was established earlier. */
  function companyRoleDupForLink(linkIdStr) {
    const ldoc = linkDocById.get(linkIdStr);
    if (!ldoc) return { duplicateCompanyRole: false, duplicateEarlierBid: null };
    const s = linkEffectiveSnap(ldoc);
    if (!s) return { duplicateCompanyRole: false, duplicateEarlierBid: null };
    const k = crKey(s.company, s.role);
    const arr = linksByCr.get(k);
    if (!arr || arr.length <= 1) {
      return { duplicateCompanyRole: false, duplicateEarlierBid: null };
    }
    const earliest = arr[0];
    if (earliest.linkId === linkIdStr) {
      return { duplicateCompanyRole: false, duplicateEarlierBid: null };
    }
    const earlierDoc = linkDocById.get(earliest.linkId);
    const url = earlierDoc?.url || '';
    return {
      duplicateCompanyRole: true,
      duplicateEarlierBid: {
        url,
        resumeId: '',
        status: '',
        createdAt: new Date(earliest.sortKey).toISOString(),
        hiddenReference: false,
      },
    };
  }

  /** @type {Map<string, { userId: string, nickname: string, avatarId: string, status: string, filled: boolean, avatarBadge: { color: string; titles: string[] } | null }[]>} */
  const groupBidsByLinkId = new Map();
  for (const b of allMemberBidsOnPageLinks) {
    const lid = String(b.groupLinkId);
    const pop = b.userId;
    const uid =
      pop && typeof pop === 'object' && pop._id != null ? String(pop._id) : String(b.userId);
    const nickname = pop && typeof pop === 'object' ? pop.nickname || '' : '';
    const rawAv = pop && typeof pop === 'object' ? pop.avatarId : '';
    const avatarId =
      rawAv && isAllowedAvatarId(String(rawAv)) ? String(rawAv).trim() : 'initial';
    if ((b.status || '') !== 'applied') continue;
    const filled = Boolean(norm(b.resumeId) || norm(b.company) || norm(b.role));
    const avatarBadge =
      pop && typeof pop === 'object' && pop.profileBadgeGrants
        ? avatarBadgeTintForGroup(pop, groupId)
        : null;
    if (!groupBidsByLinkId.has(lid)) groupBidsByLinkId.set(lid, []);
    groupBidsByLinkId.get(lid).push({
      userId: uid,
      nickname,
      avatarId,
      status: b.status || '',
      filled,
      avatarBadge,
    });
  }
  for (const arr of groupBidsByLinkId.values()) {
    arr.sort(
      (a, b) =>
        (a.nickname || '').localeCompare(b.nickname || '', undefined, {
          sensitivity: 'base',
        }) || a.userId.localeCompare(b.userId)
    );
  }

  const urlCountMap = new Map(urlDupCounts.map((x) => [x._id, x.count]));

  const linkIdsForDup = [...new Set(allUserBidsForDup.map((b) => b.groupLinkId))];
  const linkDocsForDup = await GroupLink.find({ _id: { $in: linkIdsForDup } })
    .select('url createdAt')
    .lean();
  const linkMetaById = new Map(
    linkDocsForDup.map((l) => [
      String(l._id),
      { url: l.url, createdAt: l.createdAt },
    ])
  );

  /** User bids grouped by job URL when that URL is listed more than once in the group. */
  const bidsByUrl = new Map();
  for (const b of allUserBidsForDup) {
    const meta = linkMetaById.get(String(b.groupLinkId));
    if (!meta) continue;
    if (new Date(meta.createdAt).getTime() < dupCutoff.getTime()) continue;
    const u = meta.url;
    if ((urlCountMap.get(u) || 0) <= 1) continue;
    if (!bidsByUrl.has(u)) bidsByUrl.set(u, []);
    bidsByUrl.get(u).push(b);
  }
  for (const arr of bidsByUrl.values()) {
    arr.sort((a, b) => {
      const ta = new Date(a.createdAt).getTime();
      const tb = new Date(b.createdAt).getTime();
      if (ta !== tb) return ta - tb;
      return String(a._id).localeCompare(String(b._id));
    });
  }

  function bidCreatedIso(b) {
    return b.createdAt instanceof Date ? b.createdAt.toISOString() : String(b.createdAt);
  }

  /**
   * Reposted URL in group: warn only if **this user** already has an earlier bid on the same URL
   * (another listing). If they only applied on this link, treat as fresh — per-user, not group-wide.
   */
  function urlDupForLinkAndBid(link, bid) {
    const groupHasUrlDup = (urlCountMap.get(link.url) || 0) > 1;
    if (!groupHasUrlDup || !bid) {
      return { linkDuplicate: false, duplicateEarlierUrlBid: null };
    }
    const arr = bidsByUrl.get(link.url);
    if (!arr || arr.length <= 1) {
      return { linkDuplicate: false, duplicateEarlierUrlBid: null };
    }
    const earliest = arr[0];
    if (String(earliest._id) === String(bid._id)) {
      return { linkDuplicate: false, duplicateEarlierUrlBid: null };
    }
    const em = linkMetaById.get(String(earliest.groupLinkId));
    return {
      linkDuplicate: true,
      duplicateEarlierUrlBid: {
        url: em?.url || '',
        resumeId: earliest.resumeId || '',
        status: earliest.status || '',
        createdAt: bidCreatedIso(earliest),
      },
    };
  }

  const bidByLink = new Map(userBidsPop.map((b) => [String(b.groupLinkId), b]));

  function junkPurgeEligibleForLink(lnk, bidsForLink) {
    if (!lnk.markedUselessAt || lnk.appliedAt) return false;
    if (bidsForLink.length > 1) return false;
    if (bidsForLink.length === 0) return true;
    const b0 = bidsForLink[0];
    const uid =
      b0.userId && typeof b0.userId === 'object' && b0.userId._id != null
        ? String(b0.userId._id)
        : String(b0.userId);
    if (uid !== String(lnk.createdByUserId)) return false;
    return !bidIdsWithInterviewForJunk.has(String(b0._id));
  }

  const rows = rawRows.map((link) => {
    const bidsForLink = allMemberBidsOnPageLinks.filter(
      (x) => String(x.groupLinkId) === String(link._id)
    );
    const bid = bidByLink.get(String(link._id));
    const peer = link._peerHint;
    const { linkDuplicate: linkDup, duplicateEarlierUrlBid } = urlDupForLinkAndBid(link, bid);
    const { duplicateCompanyRole: dupCr, duplicateEarlierBid } = companyRoleDupForLink(
      String(link._id)
    );
    const warnCo =
      bid && norm(bid.company)
        ? norm(bid.company)
        : peer && norm(peer.company)
          ? norm(peer.company)
          : '';
    /** Same idea as duplicate warnings: the bid that led to an interview is not flagged. */
    let companyInterviewWarning = false;
    if (warnCo) {
      const hasInterviewAtCompany = interviews.some((i) => norm(i.company) === warnCo);
      if (hasInterviewAtCompany) {
        const bidLedInterview =
          bid && interviews.some((i) => i.bidId && String(i.bidId) === String(bid._id));
        companyInterviewWarning = !bidLedInterview;
      }
    }

    return {
      link: {
        id: link._id,
        url: link.url,
        sharedJobDescription: link.sharedJobDescription || '',
        createdAt: link.createdAt,
        updatedAt: link.updatedAt,
        createdByUserId: String(link.createdByUserId),
        markedUselessAt: link.markedUselessAt
          ? new Date(link.markedUselessAt).toISOString()
          : null,
        appliedAt: link.appliedAt ? new Date(link.appliedAt).toISOString() : null,
        junkPurgeEligible: junkPurgeEligibleForLink(link, bidsForLink),
        createdBy: creatorById.get(String(link.createdByUserId)) ?? {
          nickname: '',
          avatarId: 'initial',
          avatarBadge: null,
        },
      },
      groupBidsOnLink: groupBidsByLinkId.get(String(link._id)) ?? [],
      linkDuplicate: linkDup,
      duplicateEarlierUrlBid,
      duplicateCompanyRole: dupCr,
      duplicateEarlierBid,
      companyInterviewWarning,
      myBid: bid
        ? {
            id: bid._id,
            resumeId: bid.resumeId,
            company: bid.company,
            role: bid.role,
            primaryStacks: bid.primaryStacks,
            status: bid.status,
            origin: bid.origin,
            jobDescription: bid.jobDescription,
            gptResumeContent: bid.gptResumeContent || '',
            comment: bid.comment,
            firstCreatedAt: bid.firstCreatedAt ?? bid.createdAt,
            updatedAt: bid.updatedAt,
            lastModifiedBy: bid.lastModifiedBy
              ? {
                  id: bid.lastModifiedBy._id,
                  nickname: bid.lastModifiedBy.nickname,
                  email: bid.lastModifiedBy.email,
                }
              : null,
          }
        : null,
    };
  });

  return {
    total,
    rows,
    from: t0.toISOString(),
    to: t1.toISOString(),
    sort: sort || 'linkCreatedAt:desc',
    filters: filters || {},
    capped: total > MAX_BID_BOARD_ROWS,
    groupTimers,
  };
}
