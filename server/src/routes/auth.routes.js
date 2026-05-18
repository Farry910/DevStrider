import { Router } from 'express';
import bcrypt from 'bcryptjs';
import { body, validationResult } from 'express-validator';
import { User } from '../models/User.js';
import { signToken } from '../utils/jwt.js';
import { requireAuth } from '../middleware/auth.js';
import { isAllowedAvatarId } from '../constants/avatarPresets.js';

const r = Router();

function publicUser(u) {
  return {
    id: u._id,
    email: u.email,
    nickname: u.nickname,
    avatarId: u.avatarId && isAllowedAvatarId(u.avatarId) ? u.avatarId : 'initial',
    platformRole: u.platformRole === 'admin' ? 'admin' : 'user',
  };
}

r.post(
  '/register',
  body('email').isEmail().normalizeEmail(),
  body('nickname').trim().isLength({ min: 1, max: 80 }),
  body('password').isLength({ min: 8, max: 128 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const { email, nickname, password } = req.body;
    const existing = await User.findOne({ email });
    if (existing) return res.status(409).json({ error: 'Email already registered' });
    const passwordHash = await bcrypt.hash(password, 12);
    const user = await User.create({ email, nickname, passwordHash });
    const token = signToken({ sub: user._id.toString(), email: user.email });
    const full = await User.findById(user._id).lean();
    return res.status(201).json({
      token,
      user: full ? await publicUser(full) : await publicUser(user),
    });
  }
);

r.post(
  '/login',
  body('email').isEmail().normalizeEmail(),
  body('password').isLength({ min: 1 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const { email, password } = req.body;
    const user = await User.findOne({ email });
    if (!user) return res.status(401).json({ error: 'Invalid credentials' });
    const ok = await bcrypt.compare(password, user.passwordHash);
    if (!ok) return res.status(401).json({ error: 'Invalid credentials' });
    const token = signToken({ sub: user._id.toString(), email: user.email });
    const u = await User.findById(user._id).lean();
    return res.json({
      token,
      user: u ? await publicUser(u) : await publicUser(user),
    });
  }
);

r.get('/me', requireAuth, async (req, res) => {
  const user = await User.findById(req.user.id).lean();
  if (!user) return res.status(404).json({ error: 'User not found' });
  return res.json({ user: await publicUser(user) });
});

r.patch(
  '/me',
  requireAuth,
  body('avatarId').trim().notEmpty(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const { avatarId } = req.body;
    if (!isAllowedAvatarId(avatarId)) {
      return res.status(400).json({ error: 'Invalid avatarId' });
    }
    const user = await User.findById(req.user.id);
    if (!user) return res.status(404).json({ error: 'User not found' });
    user.avatarId = avatarId;
    await user.save();
    const u = await User.findById(user._id).lean();
    return res.json({ user: u ? await publicUser(u) : await publicUser(user) });
  }
);

export default r;
