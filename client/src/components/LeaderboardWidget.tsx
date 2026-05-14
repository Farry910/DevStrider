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

export function LeaderboardWidget({ groupId }: Props) {
  const q = useQuery({
    queryKey: ['leaderboard', groupId] as const,
    enabled: !!groupId,
    queryFn: () => getLeaderboard(groupId),
    refetchOnWindowFocus: true,
  });

  const rows = q.data?.rows ?? [];
  const maxScore = rows.reduce((m, r) => (r.score > m ? r.score : m), 0);
  const callerRow = rows.find((r) => r.isCaller);

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
        <Typography variant="subtitle1">Group leaderboard</Typography>
        <Typography variant="caption" color="text.secondary">
          Current UTC month
        </Typography>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Ranked by the overview score (see Group settings → score weights). Members who opted out of the
        leaderboard appear as anonymous.
      </Typography>
      {q.isLoading && <LinearProgress />}
      {q.isError && (
        <Typography variant="caption" color="error.main">
          Could not load leaderboard.
        </Typography>
      )}
      {!q.isLoading && rows.length === 0 && (
        <Typography variant="caption" color="text.secondary">
          No members to rank yet.
        </Typography>
      )}
      <Stack spacing={1}>
        {rows.map((row) => {
          const pct = maxScore > 0 ? Math.max(0, Math.min(100, (row.score / maxScore) * 100)) : 0;
          const label = row.anonymous ? 'Anonymous member' : row.nickname || 'Unknown';
          return (
            <Box
              key={row.userId}
              sx={{
                p: 1,
                borderRadius: 1,
                border: 1,
                borderColor: row.isCaller ? 'primary.main' : 'divider',
                bgcolor: row.isCaller ? 'action.hover' : 'transparent',
              }}
            >
              <Stack direction="row" alignItems="center" spacing={1}>
                <Typography
                  variant="caption"
                  color="text.secondary"
                  sx={{ minWidth: 28, textAlign: 'right', fontWeight: 600 }}
                >
                  #{row.rank}
                </Typography>
                <Tooltip title={row.isCaller ? 'You' : label}>
                  <Avatar
                    src={row.anonymous ? undefined : presetAvatarSrc(row.avatarId) ?? undefined}
                    sx={{
                      width: 28,
                      height: 28,
                      fontSize: '0.75rem',
                      bgcolor: row.anonymous ? 'action.disabledBackground' : 'primary.dark',
                    }}
                  >
                    {row.anonymous ? '?' : (label || '?').trim().charAt(0).toUpperCase()}
                  </Avatar>
                </Tooltip>
                <Box sx={{ flex: 1, minWidth: 0 }}>
                  <Stack direction="row" alignItems="center" justifyContent="space-between">
                    <Typography variant="body2" noWrap fontWeight={row.isCaller ? 600 : 400}>
                      {label}
                      {row.isCaller ? ' (you)' : ''}
                    </Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                      {row.score.toFixed(1)}
                    </Typography>
                  </Stack>
                  <LinearProgress
                    variant="determinate"
                    value={pct}
                    color={row.isCaller ? 'primary' : 'inherit'}
                    sx={{ height: 6, borderRadius: 3, mt: 0.5 }}
                  />
                </Box>
              </Stack>
            </Box>
          );
        })}
      </Stack>
      {callerRow && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1.5 }}>
          You're #{callerRow.rank} of {rows.length}.
        </Typography>
      )}
    </Paper>
  );
}
