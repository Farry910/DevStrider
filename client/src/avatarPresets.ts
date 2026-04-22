/** Must match `server/src/constants/avatarPresets.js` and files in `public/avatars/`. */
export const AVATAR_PRESET_IDS = [
  'initial',
  'preset-1',
  'preset-2',
  'preset-3',
  'preset-4',
  'preset-5',
  'preset-6',
] as const;

export type AvatarId = (typeof AVATAR_PRESET_IDS)[number];

export function isAvatarId(v: string): v is AvatarId {
  return (AVATAR_PRESET_IDS as readonly string[]).includes(v);
}

export function presetAvatarSrc(avatarId: string): string | null {
  if (avatarId === 'initial') return null;
  return `/avatars/${avatarId}.svg`;
}
