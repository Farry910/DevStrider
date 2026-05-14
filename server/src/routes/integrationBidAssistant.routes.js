import { Router } from 'express';
import { body, validationResult } from 'express-validator';
import mongoose from 'mongoose';
import { GroupLink } from '../models/GroupLink.js';
import { UserBid } from '../models/UserBid.js';
import { requireAuth } from '../middleware/auth.js';
import { assertGroupMember } from '../services/membership.js';
import { normalizeGroupUrl, normalizeGroupUrlBase } from '../utils/urlNorm.js';
import { parseFastFeedLine, splitTrailingFastFeed } from '../utils/parseFastFeed.js';
import { emitBidBoardInvalidate } from '../socket/hexGameSocket.js';
import { awardAchievementsAsync } from '../services/achievementService.js';
import { norm } from '../services/text.js';
import { persistBidAssistantActivity } from '../services/bidAssistantActivityLog.js';

function fastFeedSpread(parsedFf) {
  if (!parsedFf) return {};
  return {
    resumeId: parsedFf.resumeId,
    company: parsedFf.company,
    role: parsedFf.role,
    primaryStacks: parsedFf.primaryStacks,
  };
}

const r = Router();
r.use(requireAuth);

function utcCalendarDayBounds(d = new Date()) {
  const y = d.getUTCFullYear();
  const m = d.getUTCMonth();
  const day = d.getUTCDate();
  const start = new Date(Date.UTC(y, m, day, 0, 0, 0, 0));
  const end = new Date(Date.UTC(y, m, day + 1, 0, 0, 0, 0));
  return { start, end };
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
 * Match saved group links when the job URL is the same “front” as a stored link.
 * Full normalized match first, then origin+path without query (tracking params), then longest shared URL prefix.
 */
async function findGroupLinkForBidAssistant(groupId, urlRaw) {
  const urlNorm = normalizeGroupUrl(urlRaw);
  const urlBase = normalizeGroupUrlBase(urlRaw);

  let link = await GroupLink.findOne({ groupId, urlNorm });
  let urlMatch = link ? 'norm' : null;

  if (!link) {
    link = await GroupLink.findOne({ groupId, url: urlRaw });
    if (link) urlMatch = 'rawUrl';
  }

  if (link && !link.urlNorm) {
    link.urlNorm = urlNorm;
    try {
      await link.save();
    } catch (e) {
      if (e?.code === 11000) {
        link = await GroupLink.findOne({ groupId, urlNorm });
        urlMatch = link ? 'norm' : urlMatch;
      } else throw e;
    }
  }
  if (link) return { link, urlMatch };

  const candidates = await GroupLink.find({ groupId }).sort({ updatedAt: -1 }).lean();
  const byBase = candidates.filter((c) => normalizeGroupUrlBase(c.url) === urlBase);
  if (byBase.length) {
    link = await GroupLink.findById(byBase[0]._id);
    return { link, urlMatch: 'base' };
  }

  let best = null;
  let bestLen = -1;
  for (const c of candidates) {
    const sb = normalizeGroupUrlBase(c.url);
    if (!sb || !urlBase) continue;
    const shorter = sb.length <= urlBase.length ? sb : urlBase;
    const longer = sb.length > urlBase.length ? sb : urlBase;
    if (shorter.length < 24) continue;
    if (longer.startsWith(shorter) && shorter.length > bestLen) {
      bestLen = shorter.length;
      best = c;
    }
  }
  if (best) {
    link = await GroupLink.findById(best._id);
    return { link, urlMatch: 'prefix' };
  }
  return { link: null, urlMatch: null };
}

function applyFastFeedToBid(bid, parsedFf) {
  if (!parsedFf) return;
  bid.resumeId = parsedFf.resumeId;
  bid.company = parsedFf.company;
  bid.role = parsedFf.role;
  bid.primaryStacks = parsedFf.primaryStacks;
  bid.status = 'applied';
}

function resolveGptAndFastFeed(gptIn, fastFeedInputRaw) {
  const ffIn = fastFeedInputRaw != null ? String(fastFeedInputRaw).trim() : '';
  let gptStored = gptIn != null ? String(gptIn) : '';
  let parsedFf = ffIn ? parseFastFeedLine(ffIn) : null;
  if (!parsedFf && gptStored) {
    const split = splitTrailingFastFeed(gptStored);
    if (split.parsed) {
      parsedFf = split.parsed;
      gptStored = split.resumePart;
    }
  }
  return { gptStored, parsedFf };
}

r.post(
  '/record-bid',
  body('groupId').isMongoId(),
  body('url').trim().isLength({ min: 5, max: 2048 }),
  body('jobDescription').optional().isString(),
  body('gptResumeContent').optional().isString(),
  body('fastFeedInput').optional().isString().isLength({ max: 2048 }),
  body('sharedJobDescription').optional().isString(),
  body('comment').optional().isString(),
  body('origin').optional().isString(),
  async (req, res) => {
    const userId = req.user.id;
    const urlRaw = req.body.url != null ? String(req.body.url).trim() : '';

    const errors = validationResult(req);
    if (!errors.isEmpty()) {
      const gid = req.body.groupId;
      await persistBidAssistantActivity({
        groupId: mongoose.isValidObjectId(gid) ? gid : undefined,
        userId,
        url: urlRaw,
        httpStatus: 400,
        error: 'Validation failed',
        meta: { validation: true, validationErrors: errors.array().length },
      });
      return res.status(400).json({ errors: errors.array() });
    }

    const groupId = req.body.groupId;
    const m = await assertGroupMember(userId, groupId);
    if (!m.ok) {
      await persistBidAssistantActivity({
        groupId,
        userId,
        url: urlRaw,
        httpStatus: m.status,
        error: m.error,
      });
      return res.status(m.status).json({ error: m.error });
    }

    const url = urlRaw;
    const urlNorm = normalizeGroupUrl(url);
    const { start: dayStart, end: dayEnd } = utcCalendarDayBounds();
    const nw = assertNowInWindow(dayStart, dayEnd);
    if (!nw.ok) {
      await persistBidAssistantActivity({
        groupId,
        userId,
        url,
        httpStatus: nw.status,
        error: nw.error,
      });
      return res.status(nw.status).json({ error: nw.error });
    }

    const { link: foundLink, urlMatch } = await findGroupLinkForBidAssistant(groupId, url);
    let link = foundLink;

    const jobDescription = req.body.jobDescription != null ? String(req.body.jobDescription) : '';
    const gptResumeRaw =
      req.body.gptResumeContent != null ? String(req.body.gptResumeContent) : '';
    const { gptStored, parsedFf } = resolveGptAndFastFeed(gptResumeRaw, req.body.fastFeedInput);
    const sharedJobDescription =
      req.body.sharedJobDescription != null ? String(req.body.sharedJobDescription) : '';
    const comment = req.body.comment != null ? String(req.body.comment) : '';
    const origin =
      req.body.origin != null && String(req.body.origin).trim()
        ? String(req.body.origin).trim()
        : 'Bid Assistant';

    const activityMetaBase = {
      hasJd: !!norm(jobDescription),
      hasGpt: !!norm(gptStored),
      hasFastFeed: !!parsedFf,
      urlMatch,
    };

    if (!link) {
      const newLink = await GroupLink.create({
        groupId,
        url,
        urlNorm,
        sharedJobDescription: sharedJobDescription || '',
        createdByUserId: userId,
      });
      const bid = await UserBid.create({
        groupId,
        userId,
        groupLinkId: newLink._id,
        status: parsedFf ? 'applied' : 'draft',
        origin,
        jobDescription,
        gptResumeContent: gptStored,
        comment,
        lastModifiedBy: userId,
        audit: [{ userId, action: 'create', snapshot: { source: 'bid-assistant' } }],
        ...fastFeedSpread(parsedFf),
      });
      emitBidBoardInvalidate(groupId);
      awardAchievementsAsync({ userId, groupId, kinds: ['daily_bids'] });
      await persistBidAssistantActivity({
        groupId,
        userId,
        url,
        httpStatus: 201,
        bidId: bid._id,
        groupLinkId: newLink._id,
        meta: { joinedExistingLink: false, updated: false, ...activityMetaBase, urlMatch: 'new' },
      });
      return res.status(201).json({
        link: newLink,
        bid,
        joinedExistingLink: false,
        updated: false,
      });
    }

    if (sharedJobDescription && norm(sharedJobDescription)) {
      link.sharedJobDescription = sharedJobDescription;
      await link.save();
    }

    let bid = await UserBid.findOne({
      groupId,
      userId,
      groupLinkId: link._id,
    });

    if (bid) {
      if (jobDescription) bid.jobDescription = jobDescription;
      if (gptStored) bid.gptResumeContent = gptStored;
      if (comment) bid.comment = comment;
      bid.origin = origin;
      bid.lastModifiedBy = userId;
      applyFastFeedToBid(bid, parsedFf);
      bid.audit.push({
        userId,
        action: 'update',
        snapshot: {
          source: 'bid-assistant',
          jobDescription: !!jobDescription,
          gptResumeContent: !!gptStored,
          fastFeed: !!parsedFf,
        },
      });
      await bid.save();
      emitBidBoardInvalidate(groupId);
      awardAchievementsAsync({ userId, groupId, kinds: ['daily_bids'] });
      await persistBidAssistantActivity({
        groupId,
        userId,
        url,
        httpStatus: 200,
        bidId: bid._id,
        groupLinkId: link._id,
        meta: { joinedExistingLink: true, updated: true, ...activityMetaBase },
      });
      return res.json({ link, bid, joinedExistingLink: true, updated: true });
    }

    bid = await UserBid.create({
      groupId,
      userId,
      groupLinkId: link._id,
      status: parsedFf ? 'applied' : 'draft',
      origin,
      jobDescription,
      gptResumeContent: gptStored,
      comment,
      lastModifiedBy: userId,
      audit: [{ userId, action: 'create', snapshot: { source: 'bid-assistant' } }],
      ...fastFeedSpread(parsedFf),
    });
    emitBidBoardInvalidate(groupId);
    await persistBidAssistantActivity({
      groupId,
      userId,
      url,
      httpStatus: 201,
      bidId: bid._id,
      groupLinkId: link._id,
      meta: { joinedExistingLink: true, updated: false, ...activityMetaBase },
    });
    return res.status(201).json({ link, bid, joinedExistingLink: true, updated: false });
  }
);

export default r;
