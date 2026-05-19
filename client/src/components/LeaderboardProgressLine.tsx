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
import { getLeaderboard } from '../api/profile';
import { presetAvatarSrc } from '../avatarPresets';

type Props = { groupId: string };

/**
 * Horizontal "race line": every member is positioned on a single axis at score / topScore. The
 * highest scorer sits at the right edge; everyone else's marker is offset proportionally so each
 * member sees their relative position at a glance. Anonymized members render as a grey dot.
 */
export function LeaderboardProgressLine({ groupId }: Props) {
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
        <Box sx={{ position: 'relative', minHeight: 56, mt: 1.5, mb: 1.5 }}>
          {/** Track line */}
          <Box
            sx={{
              position: 'absolute',
              left: 24,
              right: 24,
              top: 28,
              height: 3,
              borderRadius: 1.5,
              bgcolor: 'divider',
            }}
          />
          {rows.map((row) => {
            const pct = topScore > 0 ? Math.max(0, Math.min(100, (row.score / topScore) * 100)) : 0;
            const label = row.anonymous ? 'Anonymous' : row.nickname || 'Unknown';
            return (
              <Tooltip
                key={row.userId}
                title={`${label}${row.isCaller ? ' (you)' : ''} · #${row.rank} · score ${row.score.toFixed(1)}`}
              >
                <Box
                  sx={{
                    position: 'absolute',
                    top: 12,
                    left: `calc(24px + (100% - 48px) * ${pct / 100})`,
                    transform: 'translateX(-50%)',
                    display: 'flex',
                    flexDirection: 'column',
                    alignItems: 'center',
                    zIndex: row.isCaller ? 2 : 1,
                  }}
                >
                  <Avatar
                    src={row.anonymous ? undefined : presetAvatarSrc(row.avatarId) ?? undefined}
                    sx={{
                      width: row.isCaller ? 36 : 28,
                      height: row.isCaller ? 36 : 28,
                      fontSize: '0.75rem',
                      bgcolor: row.anonymous ? 'action.disabledBackground' : 'primary.dark',
                      border: row.isCaller ? '3px solid' : '2px solid',
                      borderColor: row.isCaller ? 'primary.main' : 'background.paper',
                      boxShadow: row.isCaller ? '0 0 0 2px rgba(25,118,210,0.25)' : 'none',
                    }}
                  >
                    {row.anonymous ? '?' : (label || '?').trim().charAt(0).toUpperCase()}
                  </Avatar>
                  <Typography
                    variant="caption"
                    sx={{
                      fontSize: '0.65rem',
                      mt: 0.25,
                      color: row.isCaller ? 'primary.main' : 'text.secondary',
                      fontWeight: row.isCaller ? 600 : 400,
                      whiteSpace: 'nowrap',
                    }}
                  >
                    {row.isCaller ? 'you' : `#${row.rank}`}
                  </Typography>
                </Box>
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
