import { Box } from '@mui/material';
import { alpha, useTheme } from '@mui/material/styles';
import type { Theme } from '@mui/material/styles';

function bidAccent(theme: Theme, status: string): string {
  const s = status.toLowerCase();
  switch (s) {
    case 'draft':
      return theme.palette.grey[500];
    case 'applied':
      return theme.palette.info.main;
    case 'screening':
      return theme.palette.primary.light;
    case 'interview':
      return theme.palette.secondary.main;
    case 'offer':
      return theme.palette.success.main;
    case 'rejected':
      return theme.palette.error.main;
    case 'withdrawn':
      return theme.palette.warning.main;
    case 'accepted':
      return theme.palette.success.dark;
    default:
      return theme.palette.grey[400];
  }
}

function interviewAccent(theme: Theme, status: string): string {
  const s = status.toLowerCase();
  switch (s) {
    case 'scheduled':
      return theme.palette.info.main;
    case 'completed':
      return theme.palette.primary.main;
    case 'passed':
      return theme.palette.success.main;
    case 'failed':
      return theme.palette.error.main;
    case 'cancelled':
      return theme.palette.warning.main;
    default:
      return theme.palette.grey[400];
  }
}

type Props = {
  status: string;
  kind: 'bid' | 'interview';
};

/**
 * Read-only status pill: left accent line, soft fill, and outline so it lines up with grid typography.
 */
export function FormatStatusBadge({ status, kind }: Props) {
  const theme = useTheme();
  const raw = status?.trim() || '—';
  const line = kind === 'bid' ? bidAccent(theme, raw) : interviewAccent(theme, raw);
  const fill = alpha(line, theme.palette.mode === 'dark' ? 0.16 : 0.12);
  const outline = alpha(line, theme.palette.mode === 'dark' ? 0.42 : 0.35);

  return (
    <Box
      component="span"
      title={raw}
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        minHeight: 26,
        maxWidth: '100%',
        px: 1.1,
        py: 0.35,
        boxSizing: 'border-box',
        borderRadius: 1,
        border: '1px solid',
        borderColor: outline,
        borderLeftWidth: 3,
        borderLeftColor: line,
        bgcolor: fill,
        color: 'text.primary',
        fontSize: '0.8125rem',
        fontWeight: 600,
        lineHeight: 1.25,
        letterSpacing: '0.01em',
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
      }}
    >
      {raw}
    </Box>
  );
}
