import { Router } from 'express';
import { param, query, validationResult } from 'express-validator';
import mongoose from 'mongoose';
import { Notification } from '../models/Notification.js';
import { requireAuth } from '../middleware/auth.js';

const r = Router();
r.use(requireAuth);

r.get(
  '/me',
  query('unreadOnly').optional().isIn(['true', 'false', '1', '0']),
  query('limit').optional().isInt({ min: 1, max: 200 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const unread = req.query.unreadOnly === 'true' || req.query.unreadOnly === '1';
    const limit = Math.min(200, Math.max(1, Number(req.query.limit) || 50));
    const filter = { userId: req.user.id };
    if (unread) filter.readAt = null;
    const [items, unreadCount] = await Promise.all([
      Notification.find(filter).sort({ createdAt: -1 }).limit(limit).lean(),
      Notification.countDocuments({ userId: req.user.id, readAt: null }),
    ]);
    return res.json({
      notifications: items.map((n) => ({
        id: n._id,
        kind: n.kind,
        payload: n.payload,
        readAt: n.readAt,
        createdAt: n.createdAt,
      })),
      unreadCount,
    });
  }
);

r.post(
  '/:id/read',
  param('id').isMongoId(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const out = await Notification.updateOne(
      { _id: new mongoose.Types.ObjectId(req.params.id), userId: req.user.id, readAt: null },
      { $set: { readAt: new Date() } }
    );
    return res.json({ updated: out.modifiedCount ?? 0 });
  }
);

r.post('/read-all', async (req, res) => {
  const out = await Notification.updateMany(
    { userId: req.user.id, readAt: null },
    { $set: { readAt: new Date() } }
  );
  return res.json({ updated: out.modifiedCount ?? 0 });
});

export default r;
