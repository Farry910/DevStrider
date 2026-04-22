/** Preset avatars shipped in `client/public/avatars/preset-{n}.svg`. */
export const AVATAR_PRESET_IDS = [
  'initial',
  'preset-1',
  'preset-2',
  'preset-3',
  'preset-4',
  'preset-5',
  'preset-6',
];

export function isAllowedAvatarId(id) {
  return AVATAR_PRESET_IDS.includes(String(id || ''));
}
