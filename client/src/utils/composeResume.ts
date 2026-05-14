import type { Profile } from '../api/profile';

/**
 * Build a resume-shaped string by combining the user's profile header with the per-bid resume body
 * (the `gptResumeContent` saved by the Bid Assistant). Returns null when there's nothing to show.
 * Owner-only: the bid board only attaches `myBid` for the current viewer, so callers don't need an
 * extra ownership check.
 */
export function composeResume(profile: Profile | null | undefined, body: string): string | null {
  const trimmedBody = (body || '').trim();
  if (!profile && !trimmedBody) return null;

  const lines: string[] = [];

  if (profile) {
    const name = profile.displayName || profile.nickname || '';
    if (name) lines.push(name.toUpperCase());
    if (profile.headline) lines.push(profile.headline);

    const contactBits: string[] = [];
    if (profile.location) contactBits.push(profile.location);
    if (profile.personalEmail) contactBits.push(profile.personalEmail);
    if (profile.phone) contactBits.push(profile.phone);
    if (contactBits.length > 0) lines.push(contactBits.join(' | '));
    if (profile.linkedinUrl) lines.push(profile.linkedinUrl);
  }

  if (trimmedBody) {
    if (lines.length > 0) lines.push('');
    lines.push(trimmedBody);
  }

  if (profile && profile.education.length > 0) {
    lines.push('');
    lines.push('Education');
    for (const e of profile.education) {
      const range =
        e.startYear && e.endYear
          ? `${e.startYear} - ${e.endYear}`
          : e.startYear
            ? `${e.startYear} -`
            : e.endYear
              ? `- ${e.endYear}`
              : '';
      const parts = [e.degree, e.school, e.location, range].filter(Boolean);
      if (parts.length > 0) lines.push(parts.join(' · '));
    }
  }

  if (profile && profile.certifications.length > 0) {
    lines.push('');
    lines.push('Certifications');
    for (const c of profile.certifications) {
      const parts = [c.name, c.issuer, c.year != null ? String(c.year) : ''].filter(Boolean);
      if (parts.length > 0) lines.push(parts.join(' · '));
    }
  }

  const out = lines.join('\n').trim();
  return out ? out : null;
}
