import { Router } from 'express';
import { PROFILE_BADGE_TYPES } from '../constants/profileBadgeTypes.js';

const r = Router();

r.get('/badge-types', (_req, res) => {
  return res.json({ badgeTypes: PROFILE_BADGE_TYPES });
});

export default r;
