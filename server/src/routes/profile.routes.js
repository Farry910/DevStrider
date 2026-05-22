import { Router } from 'express';
import { body, validationResult } from 'express-validator';
import { User } from '../models/User.js';
import { requireAuth } from '../middleware/auth.js';

const r = Router();
r.use(requireAuth);

function publicProfile(u) {
  if (!u) return null;
  return {
    id: u._id,
    email: u.email,
    nickname: u.nickname,
    avatarId: u.avatarId || 'initial',
    displayName: u.displayName || '',
    headline: u.headline || '',
    location: u.location || '',
    phone: u.phone || '',
    personalEmail: u.personalEmail || '',
    linkedinUrl: u.linkedinUrl || '',
    timezone: u.timezone || 'UTC',
    education: Array.isArray(u.education) ? u.education : [],
    certifications: Array.isArray(u.certifications) ? u.certifications : [],
    goals: {
      bidsPerDay: u.goals?.bidsPerDay ?? 0,
      interviewsPerWeek: u.goals?.interviewsPerWeek ?? 0,
      offersPerMonth: u.goals?.offersPerMonth ?? 0,
    },
    showOnLeaderboard: u.showOnLeaderboard !== false,
  };
}

r.get('/me', async (req, res) => {
  const u = await User.findById(req.user.id).lean();
  if (!u) return res.status(404).json({ error: 'User not found' });
  return res.json({ profile: publicProfile(u) });
});

/** Best-effort IANA timezone check. Tries to construct a DateTimeFormat with the given zone. */
function isValidIanaTimezone(tz) {
  if (typeof tz !== 'string' || tz.length === 0 || tz.length > 64) return false;
  try {
    new Intl.DateTimeFormat('en-US', { timeZone: tz });
    return true;
  } catch {
    return false;
  }
}

r.patch(
  '/me',
  body('nickname').optional().isString().trim().isLength({ min: 1, max: 80 }),
  body('displayName').optional().isString().isLength({ max: 200 }),
  body('headline').optional().isString().isLength({ max: 200 }),
  body('location').optional().isString().isLength({ max: 200 }),
  body('phone').optional().isString().isLength({ max: 60 }),
  body('personalEmail').optional().isString().isLength({ max: 200 }),
  body('linkedinUrl').optional().isString().isLength({ max: 500 }),
  body('timezone').optional().isString().isLength({ max: 64 }),
  body('education').optional().isArray({ max: 20 }),
  body('certifications').optional().isArray({ max: 20 }),
  body('showOnLeaderboard').optional().isBoolean(),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const u = await User.findById(req.user.id);
    if (!u) return res.status(404).json({ error: 'User not found' });
    const patch = req.body || {};
    for (const k of [
      'nickname',
      'displayName',
      'headline',
      'location',
      'phone',
      'personalEmail',
      'linkedinUrl',
    ]) {
      if (patch[k] !== undefined) u[k] = String(patch[k]).trim();
    }
    if (patch.timezone !== undefined) {
      const tz = String(patch.timezone).trim();
      if (tz && !isValidIanaTimezone(tz)) {
        return res.status(400).json({ error: 'Invalid IANA timezone' });
      }
      u.timezone = tz || 'UTC';
    }
    if (Array.isArray(patch.education)) {
      u.education = patch.education.map((e) => ({
        degree: String(e?.degree ?? '').trim(),
        school: String(e?.school ?? '').trim(),
        location: String(e?.location ?? '').trim(),
        startYear: Number.isFinite(Number(e?.startYear)) ? Number(e.startYear) : null,
        endYear: Number.isFinite(Number(e?.endYear)) ? Number(e.endYear) : null,
      }));
    }
    if (Array.isArray(patch.certifications)) {
      u.certifications = patch.certifications.map((c) => ({
        name: String(c?.name ?? '').trim(),
        issuer: String(c?.issuer ?? '').trim(),
        year: Number.isFinite(Number(c?.year)) ? Number(c.year) : null,
      }));
    }
    if (patch.showOnLeaderboard !== undefined) {
      u.showOnLeaderboard = Boolean(patch.showOnLeaderboard);
    }
    await u.save();
    return res.json({ profile: publicProfile(u.toObject()) });
  }
);

r.patch(
  '/me/goals',
  body('bidsPerDay').optional().isInt({ min: 0, max: 1000 }),
  body('interviewsPerWeek').optional().isInt({ min: 0, max: 1000 }),
  body('offersPerMonth').optional().isInt({ min: 0, max: 1000 }),
  async (req, res) => {
    const errors = validationResult(req);
    if (!errors.isEmpty()) return res.status(400).json({ errors: errors.array() });
    const u = await User.findById(req.user.id);
    if (!u) return res.status(404).json({ error: 'User not found' });
    const g = u.goals || {};
    if (req.body.bidsPerDay !== undefined) g.bidsPerDay = Number(req.body.bidsPerDay);
    if (req.body.interviewsPerWeek !== undefined) g.interviewsPerWeek = Number(req.body.interviewsPerWeek);
    if (req.body.offersPerMonth !== undefined) g.offersPerMonth = Number(req.body.offersPerMonth);
    u.goals = g;
    await u.save();
    return res.json({ goals: publicProfile(u.toObject()).goals });
  }
);

export default r;
