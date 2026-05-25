/**
 * Must match `server/src/constants/avatarPresets.js` and the files in `public/avatars/`.
 * `preset-N` are DiceBear avataaars SVGs (CC0); `animal-*` are Twemoji emoji SVGs (CC-BY 4.0).
 * See `client/public/avatars/NOTICE.md` for attribution.
 */
export const AVATAR_PRESET_IDS = [
  'initial',
  'preset-1',
  'preset-2',
  'preset-3',
  'preset-4',
  'preset-5',
  'preset-6',
  'preset-7',
  'preset-8',
  'preset-9',
  'preset-10',
  'preset-11',
  'preset-12',
  'preset-13',
  'preset-14',
  'animal-cat',
  'animal-dog',
  'animal-fox',
  'animal-panda',
  'animal-koala',
  'animal-lion',
  'animal-tiger',
  'animal-frog',
  'animal-owl',
  'animal-bear',
  'animal-penguin',
  'animal-rabbit',
] as const;

export type AvatarId = (typeof AVATAR_PRESET_IDS)[number];

export function isAvatarId(v: string): v is AvatarId {
  return (AVATAR_PRESET_IDS as readonly string[]).includes(v);
}

export function presetAvatarSrc(avatarId: string): string | null {
  if (avatarId === 'initial') return null;
  return `/avatars/${avatarId}.svg`;
}

/**
 * Human-readable label for the avatar picker. Falls back to the raw id when an unknown value
 * sneaks in (e.g. a legacy db value after a rename).
 */
export function avatarLabel(avatarId: string): string {
  if (avatarId === 'initial') return 'Initial';
  if (avatarId.startsWith('preset-')) return `Person ${avatarId.slice('preset-'.length)}`;
  if (avatarId.startsWith('animal-')) {
    const name = avatarId.slice('animal-'.length);
    return name.charAt(0).toUpperCase() + name.slice(1);
  }
  return avatarId;
}
