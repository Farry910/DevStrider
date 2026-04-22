/** Format: resumeId, Company, Role, skill1, … — mirrors client; optional legacy [...] wrapper. */
export function parseFastFeedLine(line) {
  const t = String(line || '').trim();
  if (!t) return null;
  let core = t;
  if (core.startsWith('[') && core.endsWith(']')) {
    core = core.slice(1, -1).trim();
  }
  const parts = core
    .split(',')
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
  if (parts.length < 3) return null;
  return {
    resumeId: parts[0],
    company: parts[1],
    role: parts[2],
    primaryStacks: parts.slice(3),
  };
}

/**
 * Fast-feed line is expected at the end of GPT output (last non-empty line that parses).
 * Returns resume text without that line.
 */
export function splitTrailingFastFeed(gptText) {
  const full = String(gptText || '');
  const lines = full.split(/\r?\n/);
  for (let i = lines.length - 1; i >= 0; i--) {
    const line = lines[i].trim();
    if (!line) continue;
    const parsed = parseFastFeedLine(line);
    if (parsed) {
      const resumePart = lines.slice(0, i).join('\n').replace(/\s+$/, '');
      return { resumePart, fastFeedLine: line, parsed };
    }
  }
  return { resumePart: full.trimEnd(), fastFeedLine: '', parsed: null };
}
