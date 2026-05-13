/**
 * Canonical form for deduping job URLs within a group (trim, lowercase URL, trim trailing slash on path).
 * The hash is kept — SPA portals (JobDiva, some Workday tenants) put the job ID in the URL fragment,
 * so stripping it would collapse different jobs into one. Side effect: `…#apply` and `…` are now
 * treated as distinct rows.
 */
export function normalizeGroupUrl(url) {
  const s = String(url || '').trim();
  if (!s) return '';
  try {
    const href = s.startsWith('http://') || s.startsWith('https://') ? s : `https://${s}`;
    const u = new URL(href);
    let path = u.pathname || '/';
    if (path.length > 1 && path.endsWith('/')) path = path.slice(0, -1);
    u.pathname = path || '/';
    return u.href.toLowerCase();
  } catch {
    return s.toLowerCase().replace(/\/+$/, '');
  }
}

/**
 * Same as normalizeGroupUrl but strips the query string so job URLs match even when the
 * browser adds tracking params (?utm_, ref=, etc.). Hash is kept (see normalizeGroupUrl).
 */
export function normalizeGroupUrlBase(url) {
  const s = String(url || '').trim();
  if (!s) return '';
  try {
    const href = s.startsWith('http://') || s.startsWith('https://') ? s : `https://${s}`;
    const u = new URL(href);
    u.search = '';
    let path = u.pathname || '/';
    if (path.length > 1 && path.endsWith('/')) path = path.slice(0, -1);
    u.pathname = path || '/';
    return u.href.toLowerCase();
  } catch {
    return s
      .toLowerCase()
      .replace(/\?.*?(?=#|$)/, '')
      .replace(/\/+$/, '');
  }
}
