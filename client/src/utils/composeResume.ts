import type { Profile, ResumeProfile } from '../api/profile';

/**
 * Format an Experience entry as a one-line header: "Role · Company · Location · 2022 - 2024".
 * Empty fields drop out. Used to substitute [Experience N] placeholders in the resume body.
 */
function formatExperience(exp: ResumeProfile['experiences'][number]): string {
  const range =
    exp.startYear && exp.endYear
      ? `${exp.startYear} - ${exp.endYear}`
      : exp.startYear
        ? `${exp.startYear} -`
        : exp.endYear
          ? `- ${exp.endYear}`
          : '';
  return [exp.role, exp.company, exp.location, range].filter(Boolean).join(' · ');
}

/**
 * Apply placeholder rules in-place on the body the user pastes from ChatGPT:
 *   [Title]            → line removed
 *   [Subtitle N]       → line removed
 *   [Experience N]     → replaced with profile.experiences[N-1] formatted as a header
 *   [Summary] [Skills] → left as literal text so the user can fill them in by hand
 *
 * Substitution is line-anchored to avoid eating surrounding bullet text; an unmatched
 * [Experience N] (no profile entry at that index) is stripped so a stray placeholder
 * doesn't leak into the final paste.
 */
function applyPlaceholders(body: string, experiences: ResumeProfile['experiences']): string {
  const lines = body.split('\n');
  const out: string[] = [];
  for (const raw of lines) {
    const trimmed = raw.trim();
    if (trimmed === '[Title]') continue;
    const subMatch = trimmed.match(/^\[Subtitle\s+\d+\]$/i);
    if (subMatch) continue;
    const expMatch = trimmed.match(/^\[Experience\s+(\d+)\]$/i);
    if (expMatch) {
      const idx = Number(expMatch[1]) - 1;
      const exp = idx >= 0 ? experiences[idx] : undefined;
      if (!exp) continue;
      const formatted = formatExperience(exp);
      if (formatted) out.push(formatted);
      continue;
    }
    out.push(raw);
  }
  /** Collapse runs of >2 blank lines from the stripped placeholders. */
  return out.join('\n').replace(/\n{3,}/g, '\n\n');
}

/**
 * Build a resume-shaped string by combining the row owner's profile header with the per-bid
 * resume body (the `gptResumeContent` saved by the Bid Assistant). Returns null when there's
 * nothing to show. `profile` is the per-group profile of the bid owner (viewer in self view,
 * bidder in caller view); falls back to the user-level profile when the group profile hasn't
 * been seeded yet.
 */
export function composeResume(
  profile: ResumeProfile | Profile | null | undefined,
  body: string
): string | null {
  const trimmedBody = (body || '').trim();
  if (!profile && !trimmedBody) return null;

  /** Only ResumeProfile has experiences; fall back to [] when given the user-level Profile. */
  const experiences =
    profile && 'experiences' in profile && Array.isArray(profile.experiences)
      ? profile.experiences
      : [];
  const processedBody = trimmedBody ? applyPlaceholders(trimmedBody, experiences).trim() : '';

  const lines: string[] = [];

  if (profile) {
    const name =
      profile.displayName ||
      ('nickname' in profile ? (profile.nickname as string) : '') ||
      '';
    if (name) lines.push(name.toUpperCase());
    if (profile.headline) lines.push(profile.headline);

    const contactBits: string[] = [];
    if (profile.location) contactBits.push(profile.location);
    if (profile.personalEmail) contactBits.push(profile.personalEmail);
    if (profile.phone) contactBits.push(profile.phone);
    if (contactBits.length > 0) lines.push(contactBits.join(' | '));
    if (profile.linkedinUrl) lines.push(profile.linkedinUrl);
  }

  if (processedBody) {
    if (lines.length > 0) lines.push('');
    lines.push(processedBody);
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
