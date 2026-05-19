import { useQuery } from '@tanstack/react-query';
import {
  Avatar,
  Box,
  LinearProgress,
  Paper,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material';
import { useTheme } from '@mui/material/styles';
import { getLeaderboard } from '../api/profile';
import { presetAvatarSrc } from '../avatarPresets';

type Props = { groupId: string };

/**
 * Horizontal "race line": every member is positioned on a single axis at score / topScore. The
 * highest scorer sits at the right edge; everyone else's marker is offset proportionally so each
 * member sees their relative position at a glance. Anonymized members render as a grey dot.
 */
export function LeaderboardProgressLine({ groupId }: Props) {
  const theme = useTheme();
  const q = useQuery({
    queryKey: ['leaderboard', groupId] as const,
    enabled: !!groupId,
    queryFn: () => getLeaderboard(groupId),
    refetchOnWindowFocus: true,
  });

  const rows = q.data?.rows ?? [];
  const topScore = rows.reduce((m, r) => (r.score > m ? r.score : m), 0);
  const callerRow = rows.find((r) => r.isCaller);

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
        <Typography variant="subtitle1">Group standing</Typography>
        <Typography variant="caption" color="text.secondary">
          Current UTC month · highest score → right
        </Typography>
      </Stack>
      {q.isLoading && <LinearProgress sx={{ mb: 1 }} />}
      {q.isError && (
        <Typography variant="caption" color="error.main">
          Could not load leaderboard.
        </Typography>
      )}
      {!q.isLoading && rows.length === 0 && (
        <Typography variant="caption" color="text.secondary">
          No scores yet.
        </Typography>
      )}
      {rows.length > 0 && (
        <Box sx={{ position: 'relative', minHeight: 44, mt: 1.5, mb: 1.5, px: 1 }}>
          {/** Track line */}
          <Box
            sx={{
              position: 'absolute',
              left: 16,
              right: 16,
              top: 21,
              height: 3,
              borderRadius: 1.5,
              bgcolor: 'divider',
            }}
          />
          {rows.map((row) => {
            const pct = topScore > 0 ? Math.max(0, Math.min(100, (row.score / topScore) * 100)) : 0;
            const label = row.anonymous ? 'Anonymous' : row.nickname || 'Unknown';
            const size = row.isCaller ? 18 : 14;
            return (
              <Tooltip
                key={row.userId}
                arrow
                placement="top"
                /** Avatar + name surfaced above the dot on hover, per spec. */
                title={
                  <Stack direction="row" spacing={1} alignItems="center" sx={{ py: 0.25 }}>
                    <Avatar
                      src={row.anonymous ? undefined : presetAvatarSrc(row.avatarId) ?? undefined}
                      sx={{
                        width: 32,
                        height: 32,
                        fontSize: '0.75rem',
                        bgcolor: row.anonymous ? 'action.disabledBackground' : 'primary.dark',
                      }}
                    >
                      {row.anonymous ? '?' : (label || '?').trim().charAt(0).toUpperCase()}
                    </Avatar>
                    <Box sx={{ minWidth: 0 }}>
                      <Typography variant="body2" fontWeight={600}>
                        {label}
                        {row.isCaller ? ' (you)' : ''}
                      </Typography>
                      <Typography variant="caption" sx={{ opacity: 0.85 }}>
                        #{row.rank} · score {row.score.toFixed(1)}
                      </Typography>
                    </Box>
                  </Stack>
                }
              >
                <Box
                  role="button"
                  tabIndex={0}
                  aria-label={`${label}, rank ${row.rank}, score ${row.score.toFixed(1)}`}
                  sx={{
                    position: 'absolute',
                    top: 22 - size / 2,
                    left: `calc(16px + (100% - 32px) * ${pct / 100})`,
                    width: size,
                    height: size,
                    borderRadius: '50%',
                    transform: 'translateX(-50%)',
                    cursor: 'pointer',
                    zIndex: row.isCaller ? 3 : row.anonymous ? 1 : 2,
                    bgcolor: row.anonymous
                      ? theme.palette.action.disabledBackground
                      : row.isCaller
                        ? theme.palette.primary.main
                        : theme.palette.primary.dark,
                    border: row.isCaller ? `3px solid ${theme.palette.background.paper}` : `2px solid ${theme.palette.background.paper}`,
                    boxShadow: row.isCaller
                      ? `0 0 0 3px ${theme.palette.primary.light}`
                      : `0 1px 2px rgba(0,0,0,0.2)`,
                    transition: 'transform 0.12s ease',
                    '&:hover': { transform: 'translateX(-50%) scale(1.25)' },
                    '&:focus-visible': {
                      outline: `2px solid ${theme.palette.primary.main}`,
                      outlineOffset: 2,
                    },
                  }}
                />
              </Tooltip>
            );
          })}
        </Box>
      )}
      {callerRow && (
        <Typography variant="caption" color="text.secondary">
          You're at {topScore > 0 ? Math.round((callerRow.score / topScore) * 100) : 0}% of the
          group leader. #{callerRow.rank} of {rows.length}.
        </Typography>
      )}
    </Paper>
  );
}
