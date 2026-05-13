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

export type BatchFastFeed = {
  resumeId: string;
  company: string;
  role: string;
  primaryStacks: string[];
};

/**
 * Batch fast feed: dash-separated `resumeId-company-role-stack1-stack2-…`.
 * Spaced dashes (` - `) inside a field are preserved as literal text (e.g. "Lead Engineer - Python - Backend").
 * Hyphenated compounds without surrounding spaces ("Full-Stack", "CI-CD") will be split — fix in the preview.
 */
export function parseBatchFastFeedLine(line: string): BatchFastFeed | null {
  const t = line.trim();
  if (!t) return null;
  const PH = '';
  const safe = t.replace(/ - /g, PH);
  const parts = safe
    .split('-')
    .map((p) => p.replace(new RegExp(PH, 'g'), ' - ').trim())
    .filter((p) => p.length > 0);
  if (parts.length < 3) return null;
  return {
    resumeId: parts[0],
    company: parts[1],
    role: parts[2],
    primaryStacks: parts.slice(3),
  };
}

export type BatchParsedRow = {
  /** 1-based line number in the original input (blank lines are skipped). */
  index: number;
  rawLine: string;
  url: string;
  fastFeedRaw: string;
  fastFeed: BatchFastFeed | null;
  valid: boolean;
  warnings: string[];
};

/**
 * Parse a multi-line batch. Each non-blank line is `URL<TAB>fastFeed`; either side may be empty.
 * URL is everything before the first tab; fast feed is the rest joined (allows tabs in fast feed).
 * Blank lines are skipped; invalid lines are kept with warnings so the user can fix them.
 */
export function parseBatchInput(text: string): BatchParsedRow[] {
  const out: BatchParsedRow[] = [];
  const lines = text.split(/\r?\n/);
  lines.forEach((rawLine, i) => {
    if (!rawLine.trim()) return;
    const tabIdx = rawLine.indexOf('\t');
    const url = (tabIdx >= 0 ? rawLine.slice(0, tabIdx) : rawLine).trim();
    const fastFeedRaw = (tabIdx >= 0 ? rawLine.slice(tabIdx + 1) : '').trim();
    const parsed = fastFeedRaw ? parseBatchFastFeedLine(fastFeedRaw) : null;
    const warnings: string[] = [];
    let valid = true;
    if (!url) {
      warnings.push('URL is required');
      valid = false;
    } else if (url.length < 5) {
      warnings.push('URL looks too short');
      valid = false;
    }
    if (fastFeedRaw && !parsed) {
      warnings.push('Fast feed needs ≥3 dash-separated fields (resumeId-company-role-…stacks)');
      valid = false;
    }
    out.push({
      index: i + 1,
      rawLine,
      url,
      fastFeedRaw,
      fastFeed: parsed,
      valid,
      warnings,
    });
  });
  return out;
}
