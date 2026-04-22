/** Format: resumeId, Company, Role, skill1, skill2, ... (commas; optional legacy [...] wrapper still accepted). */
export function parseFastFeedLine(line: string) {
  const t = line.trim();
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
