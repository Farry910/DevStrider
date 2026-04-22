import { useMemo } from 'react';
import { Avatar, Tooltip } from '@mui/material';
import { alpha, useTheme } from '@mui/material/styles';
import { presetAvatarSrc } from '../avatarPresets';

export type LinkCreatorAvatarBadge = {
  color: string;
  titles: string[];
};

export type LinkCreator = {
  nickname: string;
  avatarId: string;
  /** When set, avatar uses this badge color as background (letter avatars); image avatars get a matching ring. */
  avatarBadge?: LinkCreatorAvatarBadge | null;
};

function initialLetter(nickname: string) {
  const t = nickname.trim();
  if (!t) return '?';
  return t[0]!.toUpperCase();
}

type Props = {
  creator: LinkCreator;
  size?: number;
};

export function LinkCreatorBadge({ creator, size = 22 }: Props) {
  const theme = useTheme();
  const { nickname, avatarId, avatarBadge } = creator;
  const src = presetAvatarSrc(avatarId);
  const hasImage = Boolean(src);
  const tintColor = avatarBadge?.color;
  const baseLabel = nickname.trim() ? `Link added by ${nickname}` : 'Link creator';
  const label =
    avatarBadge?.titles?.length && avatarBadge.titles.length > 0
      ? `${baseLabel} · ${avatarBadge.titles.join(', ')}`
      : baseLabel;

  const sx = useMemo(() => {
    const base = {
      width: size,
      height: size,
      flexShrink: 0,
      fontSize: Math.round(size * 0.45),
    };
    if (tintColor && hasImage) {
      return {
        ...base,
        bgcolor: 'primary.dark',
        color: 'primary.contrastText',
        border: '2px solid',
        borderColor: alpha(tintColor, 0.9),
        boxShadow: `0 0 0 2px ${tintColor}`,
      };
    }
    if (tintColor && !hasImage) {
      return {
        ...base,
        bgcolor: tintColor,
        color: '#fff',
        border: '2px solid',
        borderColor: alpha(tintColor, 0.85),
        boxShadow: `inset 0 0 0 1px ${alpha('#000', 0.15)}`,
      };
    }
    return {
      ...base,
      bgcolor: 'primary.dark',
      color: 'primary.contrastText',
      border: '2px solid',
      borderColor: alpha(theme.palette.primary.light, 0.55),
      boxShadow: `inset 0 0 0 1px ${alpha(theme.palette.common.black, 0.2)}, 0 0 0 1px ${alpha(theme.palette.divider, 0.9)}`,
    };
  }, [size, tintColor, hasImage, theme]);

  return (
    <Tooltip title={label}>
      <Avatar src={src ?? undefined} alt={nickname || 'Creator'} sx={sx}>
        {initialLetter(nickname)}
      </Avatar>
    </Tooltip>
  );
}
