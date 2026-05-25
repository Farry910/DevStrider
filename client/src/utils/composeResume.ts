import type { Profile, ResumeProfile } from '../api/profile';

/**
 * Output of composeResume. `text` is the plain string the copy button puts on the clipboard;
 * `boldLines` is the subset of `text` lines (by verbatim string) the viewer should render as
 * bold/larger — currently the experience header lines we synthesize from
 * `[Subtitle N]` + profile.experiences[N-1]. Plain text stays paste-ready.
 */
export type ComposedResume = {
  text: string;
  boldLines: string[];
};

function nameForProfile(p: ResumeProfile | Profile | null | undefined): string {
  if (!p) return '';
  if (p.displayName) return p.displayName;
  return 'nickname' in p && p.nickname ? (p.nickname as string) : '';
}

function formatExperienceHeader(
  role: string,
  exp: ResumeProfile['experiences'][number] | undefined
): string {
  if (!exp) return role.trim();
  const range =
    exp.startYear && exp.endYear
      ? `${exp.startYear} - ${exp.endYear}`
      : exp.startYear
        ? `${exp.startYear} -`
        : exp.endYear
          ? `- ${exp.endYear}`
          : '';
  return [role, exp.company, exp.location, range]
    .map((s) => (s || '').trim())
    .filter(Boolean)
    .join(' · ');
}

/**
 * Walk the bid body line-by-line and apply substitution rules:
 *   `[Title]`/`[Title]:` / `[FolderName]`/`[FolderName]:` / bare `Edit`
 *     → strip the line entirely.
 *   `[Subtitle N]`/`[Subtitle N]:`
 *     → strip the marker AND consume the next non-empty content line as roleN[N].
 *   `[Experience N]`/`[Experience N]:`
 *     → replace with a header `roleN[N] · company · location · 2022 - 2024`, drawn from
 *       profile.experiences[N-1]. Falls back gracefully when a piece is missing. Header
 *       strings are recorded in `boldLines` so the viewer can render them bigger.
 *   First non-empty line that matches the profile name (case-insensitive)
 *     → strip (legacy template variant where GPT prepended the user's name).
 *   `[Summary]`/`[Summary]:` / `[Skills]`/`[Skills]:` lines pass through literal.
 */
function applyPlaceholders(
  body: string,
  experiences: ResumeProfile['experiences'],
  profileName: string
): { lines: string[]; boldSet: Set<string> } {
  const inLines = body.split('\n');
  const roleByIndex = new Map<number, string>();
  const stripIndex = new Set<number>();
  /** Pre-pass 1: capture each [Subtitle N] role from the next non-empty line, mark both stripped. */
  for (let i = 0; i < inLines.length; i++) {
    const m = inLines[i].trim().match(/^\[subtitle\s+(\d+)\]:?\s*$/i);
    if (!m) continue;
    stripIndex.add(i);
    for (let j = i + 1; j < inLines.length; j++) {
      if (inLines[j].trim() === '') continue;
      roleByIndex.set(Number(m[1]), inLines[j].trim());
      stripIndex.add(j);
      break;
    }
  }
  /** Pre-pass 2: drop a leading "JOSHUA"-style line that duplicates the profile display name. */
  if (profileName) {
    const pname = profileName.trim().toLowerCase();
    for (let i = 0; i < inLines.length; i++) {
      const t = inLines[i].trim();
      if (!t) continue;
      if (t.toLowerCase() === pname) stripIndex.add(i);
      break;
    }
  }

  const out: string[] = [];
  const boldSet = new Set<string>();
  for (let i = 0; i < inLines.length; i++) {
    if (stripIndex.has(i)) continue;
    const raw = inLines[i];
    const trimmed = raw.trim();

    if (/^\[title\]:?\s*$/i.test(trimmed)) continue;
    if (/^\[foldername\]:?\s*$/i.test(trimmed)) continue;
    if (trimmed.toLowerCase() === 'edit') continue;

    const expMatch = trimmed.match(/^\[experience\s+(\d+)\]:?\s*$/i);
    if (expMatch) {
      const idx = Number(expMatch[1]) - 1;
      const exp = idx >= 0 ? experiences[idx] : undefined;
      const role = roleByIndex.get(idx + 1) ?? '';
      const header = formatExperienceHeader(role, exp);
      if (header) {
        out.push(header);
        boldSet.add(header);
      }
      continue;
    }

    out.push(raw);
  }
  return { lines: out, boldSet };
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
): ComposedResume | null {
  const trimmedBody = (body || '').trim();
  if (!profile && !trimmedBody) return null;

  /** Only ResumeProfile has experiences; fall back to [] when given the user-level Profile. */
  const experiences =
    profile && 'experiences' in profile && Array.isArray(profile.experiences)
      ? profile.experiences
      : [];

  const placeholderResult = trimmedBody
    ? applyPlaceholders(trimmedBody, experiences, nameForProfile(profile))
    : { lines: [], boldSet: new Set<string>() };
  const processedBody = placeholderResult.lines
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();

  const lines: string[] = [];

  if (profile) {
    const name = nameForProfile(profile);
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

  const text = lines.join('\n').trim();
  if (!text) return null;
  return { text, boldLines: [...placeholderResult.boldSet] };
}
