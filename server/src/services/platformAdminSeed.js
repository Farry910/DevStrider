import bcrypt from 'bcryptjs';
import { User } from '../models/User.js';

/**
 * Ensure a platform admin user exists with the configured credentials. Idempotent: safe to call
 * on every boot. Promotes an existing user with the matching email if found; otherwise creates one.
 *
 * Credentials come from env (PLATFORM_ADMIN_EMAIL / PLATFORM_ADMIN_PASSWORD) so they can be rotated
 * without a code change in production. The defaults are convenient for development.
 */
export async function ensurePlatformAdmin() {
  const email = String(process.env.PLATFORM_ADMIN_EMAIL || 'admin@devstrider.com')
    .trim()
    .toLowerCase();
  const password = String(process.env.PLATFORM_ADMIN_PASSWORD || 'Strongpass@123456');
  const nickname = String(process.env.PLATFORM_ADMIN_NICKNAME || 'Platform Admin').trim();

  if (!email || !password) {
    console.warn('[platformAdminSeed] Missing email or password; skipping admin seed');
    return;
  }

  const existing = await User.findOne({ email });
  if (existing) {
    let dirty = false;
    if (existing.platformRole !== 'admin') {
      existing.platformRole = 'admin';
      dirty = true;
    }
    /** Re-hash the password every boot only if env explicitly opts in — usually you want to leave
     * the existing hash alone so a manual password change isn't silently overwritten. */
    if (process.env.PLATFORM_ADMIN_RESET_PASSWORD === '1') {
      existing.passwordHash = await bcrypt.hash(password, 12);
      dirty = true;
    }
    if (dirty) await existing.save();
    return;
  }

  const passwordHash = await bcrypt.hash(password, 12);
  await User.create({
    email,
    passwordHash,
    nickname,
    platformRole: 'admin',
  });
  console.log(`[platformAdminSeed] Created platform admin <${email}>`);
}
