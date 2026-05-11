import { Router } from 'express';
import { body, param, query, validationResult } from 'express-validator';
import { GroupLink } from '../models/GroupLink.js';
import { UserBid, BID_STATUSES } from '../models/UserBid.js';
import { requireAuth } from '../middleware/auth.js';
import { assertGroupCreator, assertGroupMember } from '../services/membership.js';
import { buildBidBoardPage } from '../services/bidBoard.js';
import { normalizeGroupUrl } from '../utils/urlNorm.js';
import { escapeRegex } from '../utils/regex.js';
import { norm } from '../services/text.js';
import { emitBidBoardInvalidate } from '../socket/hexGameSocket.js';
import { purgeEligibleJunkLinks } from '../services/junkLinkPurge.js';

const r = Router();
r.use(requireAuth);

/** All bids for current user in group (e.g. pick bid when scheduling interview). */
r.get(
  '/groups/:groupId/my-bids',
  param('groupId').isMongoId(),
  query('search').optional().trim().isLength({ max: 200 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const qRaw = req.query.search != null ? String(req.query.search).trim() : '';
    const filter = {
      groupId: req.params.groupId,
      userId: req.user.id,
    };
    if (qRaw.length > 0) {
      const re = new RegExp(escapeRegex(qRaw), 'i');
      filter.$or = [{ company: re }, { role: re }, { primaryStacks: re }];
    }
    const bids = await UserBid.find(filter)
      .populate('groupLinkId', 'url sharedJobDescription createdAt')
      .sort({ updatedAt: -1 })
      .lean();
    return res.json({ bids });
  }
);

r.get(
  '/groups/:groupId/bid-board',
  param('groupId').isMongoId(),
  query('from').notEmpty().isISO8601(),
  query('to').notEmpty().isISO8601(),
  query('sort').optional().isString(),
  query('f_url').optional().isString(),
  query('f_resumeId').optional().isString(),
  query('f_company').optional().isString(),
  query('f_role').optional().isString(),
  query('f_stacks').optional().isString(),
  query('f_status').optional().isString(),
  query('f_origin').optional().isString(),
  query('f_sharedJd').optional().isString(),
  query('f_privateJd').optional().isString(),
  query('f_comment').optional().isString(),
  query('excludeLinkOnly').optional().isIn(['true', '1', 'false', '0']),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const filters = {
      url: req.query.f_url,
      resumeId: req.query.f_resumeId,
      company: req.query.f_company,
      role: req.query.f_role,
      stacks: req.query.f_stacks,
      status: req.query.f_status,
      origin: req.query.f_origin,
      sharedJd: req.query.f_sharedJd,
      privateJd: req.query.f_privateJd,
      comment: req.query.f_comment,
    };
    const excludeLinkOnly =
      req.query.excludeLinkOnly === 'true' || req.query.excludeLinkOnly === '1';
    const data = await buildBidBoardPage({
      groupId: req.params.groupId,
      userId: req.user.id,
      sort: req.query.sort,
      filters,
      from: req.query.from,
      to: req.query.to,
      excludeLinkOnly,
    });
    return res.json(data);
  }
);

function utcCalendarDayBounds(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = d.getUTCMonth();
  const day = d.getUTCDate();
  const start = new Date(Date.UTC(y, m, day, 0, 0, 0, 0));
  const end = new Date(Date.UTC(y, m, day + 1, 0, 0, 0, 0));
  return { start, end };
}

function parseBiddingWindow(fromRaw, toRaw) {
  const winStart = new Date(fromRaw);
  const winEnd = new Date(toRaw);
  if (Number.isNaN(winStart.getTime()) || Number.isNaN(winEnd.getTime()) || winEnd <= winStart) {
    return { ok: false, status: 400, error: 'Invalid bidding window (from / to)' };
  }
  return { ok: true, winStart, winEnd };
}

function assertNowInWindow(winStart, winEnd) {
  const t = Date.now();
  if (t < winStart.getTime() || t >= winEnd.getTime()) {
    return {
      ok: false,
      status: 403,
      error: 'Bidding is only allowed during the current calendar day.',
    };
  }
  return { ok: true };
}

/**
 * Same day rule as `buildBidBoardPage`: row is on the board for [from,to) if the link was created
 * that day or your bid was updated that day. Allows editing past days when the client passes that
 * day's window (no longer requires "now" to fall in the window).
 */
async function assertBidInBoardDaySlice(bid, groupId, fromRaw, toRaw) {
  const w = parseBiddingWindow(fromRaw, toRaw);
  if (!w.ok) return w;
  const t0 = w.winStart.getTime();
  const t1 = w.winEnd.getTime();

  const link = await GroupLink.findOne({
    _id: bid.groupLinkId,
    groupId,
  })
    .select('createdAt')
    .lean();
  if (!link) {
    return { ok: false, status: 404, error: 'Link not found' };
  }

  const lc = new Date(link.createdAt).getTime();
  const bu = new Date(bid.updatedAt).getTime();
  const inSlice = (lc >= t0 && lc < t1) || (bu >= t0 && bu < t1);
  if (!inSlice) {
    return {
      ok: false,
      status: 403,
      error: 'This bid is not part of the selected calendar day.',
    };
  }
  return { ok: true };
}

/** Add a new shared link row + your bid row (inline flow — no separate form). */
r.post(
  '/groups/:groupId/links',
  param('groupId').isMongoId(),
  body('url').trim().isLength({ min: 5, max: 2048 }),
  body('from').optional().isISO8601(),
  body('to').optional().isISO8601(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const url = req.body.url.trim();
    const urlNorm = normalizeGroupUrl(url);

    let dayStart;
    let dayEnd;
    const fromRaw = req.body.from;
    const toRaw = req.body.to;
    if (fromRaw != null && String(fromRaw).trim() && toRaw != null && String(toRaw).trim()) {
      dayStart = new Date(fromRaw);
      dayEnd = new Date(toRaw);
      if (Number.isNaN(dayStart.getTime()) || Number.isNaN(dayEnd.getTime()) || dayEnd <= dayStart) {
        return res.status(400).json({ error: 'Invalid from/to day range' });
      }
    } else if (fromRaw != null || toRaw != null) {
      return res.status(400).json({ error: 'Both from and to are required when specifying a day range' });
    } else {
      const b = utcCalendarDayBounds();
      dayStart = b.start;
      dayEnd = b.end;
    }

    const nw = assertNowInWindow(dayStart, dayEnd);
    if (!nw.ok) return res.status(nw.status).json({ error: nw.error });

    let link =
      (await GroupLink.findOne({ groupId: req.params.groupId, urlNorm })) ||
      (await GroupLink.findOne({ groupId: req.params.groupId, url }));
    if (link && !link.urlNorm) {
      link.urlNorm = urlNorm;
      try {
        await link.save();
      } catch (e) {
        if (e?.code === 11000) {
          link = await GroupLink.findOne({ groupId: req.params.groupId, urlNorm });
        } else throw e;
      }
    }

    if (link) {
      const existingBid = await UserBid.findOne({
        groupId: req.params.groupId,
        userId: req.user.id,
        groupLinkId: link._id,
      });
      if (existingBid) {
        return res.status(409).json({
          error: 'You already have a bid on this job link.',
          code: 'DUPLICATE_USER_BID_ON_LINK',
        });
      }
      const bid = await UserBid.create({
        groupId: req.params.groupId,
        userId: req.user.id,
        groupLinkId: link._id,
        status: 'draft',
        lastModifiedBy: req.user.id,
        audit: [{ userId: req.user.id, action: 'create' }],
      });
      emitBidBoardInvalidate(req.params.groupId);
      return res.status(201).json({ link, bid, joinedExistingLink: true });
    }

    /** Sequential writes (no transaction) so standalone mongod works without replica set. */
    const newLink = await GroupLink.create({
      groupId: req.params.groupId,
      url,
      urlNorm,
      sharedJobDescription: '',
      createdByUserId: req.user.id,
    });
    const bid = await UserBid.create({
      groupId: req.params.groupId,
      userId: req.user.id,
      groupLinkId: newLink._id,
      status: 'draft',
      lastModifiedBy: req.user.id,
      audit: [{ userId: req.user.id, action: 'create' }],
    });
    emitBidBoardInvalidate(req.params.groupId);
    return res.status(201).json({ link: newLink, bid, joinedExistingLink: false });
  }
);

/** Create your bid on an existing shared link (bid-ready row). */
r.post(
  '/groups/:groupId/links/:linkId/my-bid',
  param('groupId').isMongoId(),
  param('linkId').isMongoId(),
  query('from').notEmpty().isISO8601(),
  query('to').notEmpty().isISO8601(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const w = parseBiddingWindow(req.query.from, req.query.to);
    if (!w.ok) return res.status(w.status).json({ error: w.error });
    const nw = assertNowInWindow(w.winStart, w.winEnd);
    if (!nw.ok) return res.status(nw.status).json({ error: nw.error });

    const link = await GroupLink.findOne({
      _id: req.params.linkId,
      groupId: req.params.groupId,
    });
    if (!link) return res.status(404).json({ error: 'Link not found' });

    const existingBid = await UserBid.findOne({
      groupId: req.params.groupId,
      userId: req.user.id,
      groupLinkId: link._id,
    });
    if (existingBid) return res.status(409).json({ error: 'You already have a bid on this link' });

    const bid = await UserBid.create({
      groupId: req.params.groupId,
      userId: req.user.id,
      groupLinkId: link._id,
      status: 'draft',
      lastModifiedBy: req.user.id,
      audit: [{ userId: req.user.id, action: 'create' }],
    });
    emitBidBoardInvalidate(req.params.groupId);
    return res.status(201).json({ bid });
  }
);

/** Update shared job description only (visible to group on this link row). */
r.patch(
  '/groups/:groupId/links/:linkId',
  param('groupId').isMongoId(),
  param('linkId').isMongoId(),
  body('sharedJobDescription').optional().isString(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const link = await GroupLink.findOne({
      _id: req.params.linkId,
      groupId: req.params.groupId,
    });
    if (!link) return res.status(404).json({ error: 'Link not found' });
    if (req.body.sharedJobDescription !== undefined) {
      link.sharedJobDescription = req.body.sharedJobDescription;
    }
    await link.save();
    emitBidBoardInvalidate(req.params.groupId);
    return res.json({ link });
  }
);

/** Link creator only: mark / unmark this posting as useless (owner can later purge eligible junk). */
r.patch(
  '/groups/:groupId/links/:linkId/useless',
  param('groupId').isMongoId(),
  param('linkId').isMongoId(),
  body('useless').isBoolean(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const link = await GroupLink.findOne({
      _id: req.params.linkId,
      groupId: req.params.groupId,
    });
    if (!link) return res.status(404).json({ error: 'Link not found' });
    if (String(link.createdByUserId) !== String(req.user.id)) {
      return res.status(403).json({ error: 'Only the link creator can mark this posting as useless' });
    }
    if (req.body.useless) {
      link.markedUselessAt = new Date();
    } else {
      link.markedUselessAt = null;
    }
    await link.save();
    emitBidBoardInvalidate(req.params.groupId);
    return res.json({ link });
  }
);

/**
 * Group owner: permanently remove junk links that were marked useless by their creator and have no
 * application activity that blocks removal.
 */
r.post('/groups/:groupId/links/refresh-junk', param('groupId').isMongoId(), async (req, res) => {
  const errors = validationResult(req);
  if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
  const m = await assertGroupCreator(req.user.id, req.params.groupId);
  if (!m.ok) return res.status(m.status).json({ error: m.error });
  const result = await purgeEligibleJunkLinks({
    groupId: req.params.groupId,
    minimumMarkedAgeMs: 0,
  });
  return res.json({ removed: result.removed, linkIds: result.linkIds });
});

function utcYesterdayBounds(now = new Date()) {
  const y = now.getUTCFullYear();
  const m = now.getUTCMonth();
  const d = now.getUTCDate();
  const start = new Date(Date.UTC(y, m, d - 1, 0, 0, 0, 0));
  const end = new Date(Date.UTC(y, m, d, 0, 0, 0, 0));
  return { start, end };
}

/** Returns how many of yesterday's (UTC) non-useless group links this user has no bid on yet. */
r.get(
  '/groups/:groupId/links/carryover-count',
  param('groupId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const { start, end } = utcYesterdayBounds();
    const links = await GroupLink.find({
      groupId: req.params.groupId,
      createdAt: { $gte: start, $lt: end },
      markedUselessAt: null,
    })
      .select('_id')
      .lean();
    if (links.length === 0) return res.json({ count: 0 });
    const linkIds = links.map((l) => l._id);
    const myBids = await UserBid.find({
      groupId: req.params.groupId,
      userId: req.user.id,
      groupLinkId: { $in: linkIds },
    })
      .select('groupLinkId')
      .lean();
    const bidSet = new Set(myBids.map((b) => String(b.groupLinkId)));
    const count = linkIds.filter((id) => !bidSet.has(String(id))).length;
    return res.json({ count });
  }
);

/**
 * Carry yesterday's empty links onto today's board for this user. Creates a draft UserBid per link
 * the user has no bid on (skipping links marked useless). The fresh bid's updatedAt anchors the link
 * to today via the bid-board's "bid touched this day" rule — link.createdAt is untouched, so other
 * members' views of yesterday are unaffected.
 */
r.post(
  '/groups/:groupId/links/carryover',
  param('groupId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const { start, end } = utcYesterdayBounds();
    const links = await GroupLink.find({
      groupId: req.params.groupId,
      createdAt: { $gte: start, $lt: end },
      markedUselessAt: null,
    })
      .select('_id')
      .lean();
    if (links.length === 0) return res.json({ carriedOver: 0, linkIds: [] });
    const linkIds = links.map((l) => l._id);
    const myBids = await UserBid.find({
      groupId: req.params.groupId,
      userId: req.user.id,
      groupLinkId: { $in: linkIds },
    })
      .select('groupLinkId')
      .lean();
    const bidSet = new Set(myBids.map((b) => String(b.groupLinkId)));
    const toCarry = linkIds.filter((id) => !bidSet.has(String(id)));
    if (toCarry.length === 0) return res.json({ carriedOver: 0, linkIds: [] });
    const createdIds = [];
    for (const linkId of toCarry) {
      try {
        const bid = await UserBid.create({
          groupId: req.params.groupId,
          userId: req.user.id,
          groupLinkId: linkId,
          status: 'draft',
          lastModifiedBy: req.user.id,
          audit: [{ userId: req.user.id, action: 'carryover' }],
        });
        createdIds.push(bid._id);
      } catch (e) {
        if (e?.code !== 11000) throw e;
      }
    }
    if (createdIds.length > 0) emitBidBoardInvalidate(req.params.groupId);
    return res.json({ carriedOver: createdIds.length, linkIds: createdIds });
  }
);

/** Update your bid columns (Resume ID, company, role, etc.). */
r.patch(
  '/groups/:groupId/bids/:bidId',
  param('groupId').isMongoId(),
  param('bidId').isMongoId(),
  query('from').notEmpty().isISO8601(),
  query('to').notEmpty().isISO8601(),
  body('resumeId').optional().isString(),
  body('company').optional().isString(),
  body('role').optional().isString(),
  body('primaryStacks').optional().isArray(),
  body('status').optional().isIn(BID_STATUSES),
  body('origin').optional().isString(),
  body('jobDescription').optional().isString(),
  body('gptResumeContent').optional().isString(),
  body('comment').optional().isString(),
  body('fromFastFeed').optional().isBoolean(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const bid = await UserBid.findOne({
      _id: req.params.bidId,
      groupId: req.params.groupId,
      userId: req.user.id,
    });
    if (!bid) return res.status(404).json({ error: 'Bid not found' });

    const gate = await assertBidInBoardDaySlice(bid, req.params.groupId, req.query.from, req.query.to);
    if (!gate.ok) return res.status(gate.status).json({ error: gate.error });

    const assign = [
      'resumeId',
      'company',
      'role',
      'primaryStacks',
      'status',
      'origin',
      'jobDescription',
      'gptResumeContent',
      'comment',
    ];
    for (const k of assign) {
      if (req.body[k] !== undefined) bid[k] = req.body[k];
    }
    if (
      req.body.fromFastFeed === true &&
      norm(bid.company) &&
      norm(bid.role) &&
      req.body.status === undefined
    ) {
      bid.status = 'applied';
    }
    bid.lastModifiedBy = req.user.id;
    bid.audit.push({
      userId: req.user.id,
      action: 'update',
      snapshot: {
        resumeId: bid.resumeId,
        company: bid.company,
        role: bid.role,
        status: bid.status,
      },
    });
    await bid.save();

    if (norm(bid.company) && norm(bid.role)) {
      const gl = await GroupLink.findOne({
        _id: bid.groupLinkId,
        groupId: bid.groupId,
      });
      if (gl && (!norm(gl.appliedCompany) || !norm(gl.appliedRole))) {
        gl.appliedCompany = String(bid.company).trim();
        gl.appliedRole = String(bid.role).trim();
        gl.appliedStacks = Array.isArray(bid.primaryStacks) ? bid.primaryStacks : [];
        gl.appliedAt = new Date();
        gl.appliedByUserId = req.user.id;
        await gl.save();
      }
    }

    emitBidBoardInvalidate(req.params.groupId);
    return res.json({ bid });
  }
);

/** Remove your bid only. Shared group links stay — they belong to the group once added. */
r.delete(
  '/groups/:groupId/bids/:bidId',
  param('groupId').isMongoId(),
  param('bidId').isMongoId(),
  query('from').notEmpty().isISO8601(),
  query('to').notEmpty().isISO8601(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const bid = await UserBid.findOne({
      _id: req.params.bidId,
      groupId: req.params.groupId,
      userId: req.user.id,
    });
    if (!bid) return res.status(404).json({ error: 'Bid not found' });

    const gate = await assertBidInBoardDaySlice(bid, req.params.groupId, req.query.from, req.query.to);
    if (!gate.ok) return res.status(gate.status).json({ error: gate.error });

    await UserBid.deleteOne({ _id: bid._id });
    emitBidBoardInvalidate(req.params.groupId);
    return res.status(204).send();
  }
);

export default r;
