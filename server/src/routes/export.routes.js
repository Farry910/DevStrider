import { Router } from 'express';
import { param, query, validationResult } from 'express-validator';
import mongoose from 'mongoose';
import { UserBid } from '../models/UserBid.js';
import { Interview } from '../models/Interview.js';
import { requireAuth } from '../middleware/auth.js';
import {
  assertGroupMember,
  getEffectiveRoles,
  watchedUserIdsFor,
} from '../services/membership.js';

const r = Router();
r.use(requireAuth);

/** Escape one CSV cell — handles commas, quotes, and newlines per RFC 4180. */
function csvCell(v) {
  if (v == null) return '';
  const s = typeof v === 'string' ? v : String(v);
  if (/[",\n\r]/.test(s)) return `"${s.replace(/"/g, '""')}"`;
  return s;
}
function csvRow(values) {
  return values.map(csvCell).join(',') + '\n';
}

function parseRange(rangeParam, fromParam, toParam) {
  if (fromParam && toParam) {
    const from = new Date(fromParam);
    const to = new Date(toParam);
    if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()) || to <= from) return null;
    return { from, to };
  }
  const now = new Date();
  if (rangeParam === 'daily') {
    const start = new Date(
      Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate(), 0, 0, 0)
    );
    const end = new Date(
      Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1, 0, 0, 0)
    );
    return { from: start, to: end };
  }
  if (rangeParam === 'monthly') {
    const start = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1, 0, 0, 0));
    const end = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1, 0, 0, 0));
    return { from: start, to: end };
  }
  /** Default: weekly (rolling 7 days). */
  const from = new Date(now.getTime() - 7 * 86400000);
  return { from, to: now };
}

/**
 * Stream a role-scoped CSV of bids or interviews for the given date range.
 * - `kind=bids`: rows the caller can see based on role (BIDDER own; CALLER+OPS watched; ADMIN all).
 * - `kind=interviews`: same scoping.
 * Uses Mongoose cursors so the whole result-set never lives in memory.
 */
r.get(
  '/groups/:groupId/export',
  param('groupId').isMongoId(),
  query('kind').isIn(['bids', 'interviews']),
  query('range').optional().isIn(['daily', 'weekly', 'monthly', 'custom']),
  query('from').optional().isISO8601(),
  query('to').optional().isISO8601(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });

    const win = parseRange(req.query.range, req.query.from, req.query.to);
    if (!win) return res.status(400).json({ error: 'Invalid window' });

    const roles = getEffectiveRoles(m.group, req.user.id);
    const gid = new mongoose.Types.ObjectId(req.params.groupId);
    /** Determine which userIds this caller can export. */
    let userIdFilter;
    if (roles.includes('admin')) {
      userIdFilter = null; // all users in group
    } else if (roles.includes('caller') || roles.includes('ops')) {
      const watches = watchedUserIdsFor(m.group, req.user.id);
      if (watches.length === 0) {
        /** No watches → empty CSV with header only. */
        userIdFilter = { $in: [] };
      } else {
        userIdFilter = { $in: watches.map((id) => new mongoose.Types.ObjectId(id)) };
      }
    } else if (roles.includes('bidder')) {
      userIdFilter = new mongoose.Types.ObjectId(req.user.id);
    } else {
      /** No applicable role — empty CSV. */
      userIdFilter = { $in: [] };
    }

    const filenameKind = req.query.kind === 'bids' ? 'bids' : 'interviews';
    const filenameRange = req.query.range || 'weekly';
    const filename = `${filenameKind}-${filenameRange}-${win.from
      .toISOString()
      .slice(0, 10)}.csv`;

    res.setHeader('Content-Type', 'text/csv; charset=utf-8');
    res.setHeader('Content-Disposition', `attachment; filename="${filename}"`);
    res.setHeader('Cache-Control', 'no-store');

    if (req.query.kind === 'bids') {
      const matchUserClause = userIdFilter ? { userId: userIdFilter } : {};
      const filter = {
        groupId: gid,
        ...matchUserClause,
        updatedAt: { $gte: win.from, $lt: win.to },
      };
      res.write(
        csvRow([
          'bidId',
          'userId',
          'userNickname',
          'company',
          'role',
          'primaryStacks',
          'status',
          'origin',
          'url',
          'firstCreatedAt',
          'updatedAt',
        ])
      );
      const cursor = UserBid.find(filter)
        .populate({ path: 'userId', select: 'nickname' })
        .populate({ path: 'groupLinkId', select: 'url' })
        .sort({ updatedAt: -1 })
        .cursor();
      for await (const doc of cursor) {
        res.write(
          csvRow([
            String(doc._id),
            String(doc.userId?._id ?? doc.userId ?? ''),
            doc.userId?.nickname || '',
            doc.company || '',
            doc.role || '',
            (doc.primaryStacks || []).join('|'),
            doc.status || '',
            doc.origin || '',
            doc.groupLinkId?.url || '',
            doc.firstCreatedAt ? new Date(doc.firstCreatedAt).toISOString() : '',
            new Date(doc.updatedAt).toISOString(),
          ])
        );
      }
      res.end();
      return;
    }

    /** kind === 'interviews' */
    const matchUserClause = userIdFilter ? { userId: userIdFilter } : {};
    const filter = {
      groupId: gid,
      ...matchUserClause,
      $or: [
        { scheduledDate: { $gte: win.from, $lt: win.to } },
        { createdAt: { $gte: win.from, $lt: win.to } },
      ],
    };
    res.write(
      csvRow([
        'interviewId',
        'userId',
        'userNickname',
        'company',
        'role',
        'recruiter',
        'interviewType',
        'status',
        'origin',
        'meetingLink',
        'scheduledDate',
        'scheduledTime',
        'durationMinutes',
        'createdAt',
        'attachedJobDescriptionAttached',
        'attachedResumeAttached',
      ])
    );
    const cursor = Interview.find(filter)
      .populate({ path: 'userId', select: 'nickname' })
      .sort({ scheduledDate: -1, createdAt: -1 })
      .cursor();
    for await (const doc of cursor) {
      res.write(
        csvRow([
          String(doc._id),
          String(doc.userId?._id ?? doc.userId ?? ''),
          doc.userId?.nickname || '',
          doc.company || '',
          doc.role || '',
          doc.recruiter || '',
          doc.interviewType || '',
          doc.status || '',
          doc.origin || '',
          doc.meetingLink || '',
          doc.scheduledDate ? new Date(doc.scheduledDate).toISOString() : '',
          doc.scheduledTime || '',
          doc.durationMinutes ?? '',
          new Date(doc.createdAt).toISOString(),
          doc.attachedJobDescription ? 'yes' : 'no',
          doc.attachedResumeContent ? 'yes' : 'no',
        ])
      );
    }
    res.end();
  }
);

/**
 * Single-interview detail with JD+resume body — for CALLER reference (separate from list export
 * to keep the list CSV small). Returns JSON, not CSV.
 */
r.get(
  '/groups/:groupId/interviews/:interviewId/attachment',
  param('groupId').isMongoId(),
  param('interviewId').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const m = await assertGroupMember(req.user.id, req.params.groupId);
    if (!m.ok) return res.status(m.status).json({ error: m.error });
    const iv = await Interview.findOne({
      _id: req.params.interviewId,
      groupId: req.params.groupId,
    })
      .select('userId attachedJobDescription attachedResumeContent attachedAt')
      .lean();
    if (!iv) return res.status(404).json({ error: 'Interview not found' });
    const roles = getEffectiveRoles(m.group, req.user.id);
    if (!roles.includes('admin') && !roles.includes('caller')) {
      /** Owner can also read their own attachment. */
      if (String(iv.userId) !== String(req.user.id)) {
        return res.status(403).json({ error: 'Forbidden' });
      }
    } else if (!roles.includes('admin')) {
      const watches = watchedUserIdsFor(m.group, req.user.id);
      if (!watches.includes(String(iv.userId))) {
        return res.status(403).json({ error: 'Interview owner not in your watches' });
      }
    }
    return res.json({
      attachedJobDescription: iv.attachedJobDescription || '',
      attachedResumeContent: iv.attachedResumeContent || '',
      attachedAt: iv.attachedAt || null,
    });
  }
);

export default r;
