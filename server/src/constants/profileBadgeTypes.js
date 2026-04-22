/** Canonical profile badge kinds users may request (group creator approves per group). */

export const PROFILE_BADGE_TYPES = [
  { key: 'mentor', label: 'Mentor', shortLabel: 'Mentor', color: '#7e57c2' },
  { key: 'verified_pro', label: 'Verified pro', shortLabel: 'Pro', color: '#42a5f5' },
  { key: 'top_contributor', label: 'Top contributor', shortLabel: 'Top', color: '#26a69a' },
  { key: 'community_guide', label: 'Community guide', shortLabel: 'Guide', color: '#ffb74d' },
  { key: 'alumni', label: 'Alumni', shortLabel: 'Alumni', color: '#78909c' },
];

export const PROFILE_BADGE_KEYS = new Set(PROFILE_BADGE_TYPES.map((b) => b.key));

/** @param {string} key */
export function getProfileBadgeType(key) {
  return PROFILE_BADGE_TYPES.find((b) => b.key === key) ?? null;
}

/** @param {string[] | null | undefined} keys */
export function sanitizeProfileBadgeKeys(keys) {
  if (!keys || !Array.isArray(keys)) return [];
  return [...new Set(keys.filter((k) => typeof k === 'string' && PROFILE_BADGE_KEYS.has(k)))];
}

/** Resolved badge payloads for API (e.g. link creator on bid board). */
export function resolveProfileBadgesForUser(keys) {
  return sanitizeProfileBadgeKeys(keys)
    .map((key) => {
      const t = getProfileBadgeType(key);
      return t
        ? { key: t.key, label: t.label, shortLabel: t.shortLabel, color: t.color }
        : null;
    })
    .filter(Boolean);
}

/** @param { { profileBadgeGrants?: { groupId: unknown; badgeKey: string }[] } | null | undefined } userDoc */
export function badgeKeysForGroupFromUserDoc(userDoc, groupIdStr) {
  const g = String(groupIdStr);
  const grants = userDoc?.profileBadgeGrants;
  if (!grants || !Array.isArray(grants)) return [];
  return sanitizeProfileBadgeKeys(
    grants.filter((x) => x && String(x.groupId) === g).map((x) => x.badgeKey)
  );
}

export function resolveProfileBadgesForUserInGroup(userDoc, groupIdStr) {
  return resolveProfileBadgesForUser(badgeKeysForGroupFromUserDoc(userDoc, groupIdStr));
}

/**
 * Ordered by catalog order; used for avatar tint (first) and tooltips.
 * @param { { profileBadgeGrants?: { groupId: unknown; badgeKey: string }[] } | null | undefined } userDoc
 */
export function orderedProfileBadgeTypesInGroup(userDoc, groupIdStr) {
  const keys = badgeKeysForGroupFromUserDoc(userDoc, groupIdStr);
  if (keys.length === 0) return [];
  const order = new Map(PROFILE_BADGE_TYPES.map((b, i) => [b.key, i]));
  const sorted = [...keys].sort((a, b) => (order.get(a) ?? 99) - (order.get(b) ?? 99));
  return sorted.map((k) => getProfileBadgeType(k)).filter(Boolean);
}

/**
 * Avatar uses the first catalog-ordered badge color; tooltip lists all approved labels.
 * @returns {{ color: string, titles: string[] } | null}
 */
export function avatarBadgeTintForGroup(userDoc, groupIdStr) {
  const types = orderedProfileBadgeTypesInGroup(userDoc, groupIdStr);
  if (types.length === 0) return null;
  return {
    color: types[0].color,
    titles: types.map((t) => t.label),
  };
}
