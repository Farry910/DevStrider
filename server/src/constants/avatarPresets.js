/**
 * Preset avatars shipped in `client/public/avatars/`. Must match the client-side list in
 * `client/src/avatarPresets.ts`. People presets are DiceBear avataaars SVGs; animal-* presets are
 * Twemoji emoji SVGs (see NOTICE.md for attribution).
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
];

export function isAllowedAvatarId(id) {
  return AVATAR_PRESET_IDS.includes(String(id || ''));
}
