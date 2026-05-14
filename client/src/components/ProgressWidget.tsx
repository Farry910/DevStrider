import { useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Box, Chip, LinearProgress, Paper, Stack, Tooltip, Typography } from '@mui/material';
import EmojiEventsIcon from '@mui/icons-material/EmojiEvents';
import { getMyAchievements } from '../api/profile';
import { getAppSocket } from '../socket/appSocket';

type Props = { groupId: string };

function Bar({
  label,
  value,
  target,
  active,
}: {
  label: string;
  value: number;
  target: number;
  active: boolean;
}) {
  const noGoal = target <= 0;
  const pct = noGoal ? 0 : Math.min(100, Math.round((value / target) * 100));
  return (
    <Box>
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 0.25 }}>
        <Typography variant="caption" color="text.secondary">
          {label}
        </Typography>
        <Stack direction="row" alignItems="center" spacing={0.5}>
          {active && (
            <Tooltip title="Goal hit for this period">
              <EmojiEventsIcon fontSize="small" sx={{ color: 'warning.main' }} />
            </Tooltip>
          )}
          <Typography variant="caption" color={active ? 'success.main' : 'text.primary'} fontWeight={600}>
            {noGoal ? `${value}` : `${value} / ${target}`}
          </Typography>
        </Stack>
      </Stack>
      <LinearProgress
        variant="determinate"
        value={noGoal ? 0 : pct}
        color={active ? 'success' : 'primary'}
        sx={{ height: 6, borderRadius: 3, opacity: noGoal ? 0.3 : 1 }}
      />
    </Box>
  );
}

export function ProgressWidget({ groupId }: Props) {
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: ['achievements', groupId, 'me'] as const,
    enabled: !!groupId,
    queryFn: () => getMyAchievements(groupId),
    refetchOnWindowFocus: true,
  });

  useEffect(() => {
    if (!groupId) return;
    const socket = getAppSocket();
    const onNew = () => {
      void qc.invalidateQueries({ queryKey: ['achievements', groupId, 'me'] });
    };
    const onBoard = (payload: { groupId?: string }) => {
      if (payload?.groupId === groupId) {
        void qc.invalidateQueries({ queryKey: ['achievements', groupId, 'me'] });
      }
    };
    socket.on('notification:new', onNew);
    socket.on('bidboard:invalidate', onBoard);
    return () => {
      socket.off('notification:new', onNew);
      socket.off('bidboard:invalidate', onBoard);
    };
  }, [groupId, qc]);

  const d = q.data;
  if (!d) return null;
  const activeKinds = new Set(d.activeBadges.map((b) => b.kind));
  const allGoalsZero =
    d.goals.bidsPerDay <= 0 && d.goals.interviewsPerWeek <= 0 && d.goals.offersPerMonth <= 0;

  return (
    <Paper variant="outlined" sx={{ p: 1.5 }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
        <Typography variant="subtitle2">Your progress</Typography>
        {d.activeBadges.length > 0 && (
          <Chip
            size="small"
            color="success"
            icon={<EmojiEventsIcon />}
            label={`${d.activeBadges.length} achieved`}
            sx={{ height: 22, '& .MuiChip-label': { px: 0.85, fontSize: '0.7rem' } }}
          />
        )}
      </Stack>
      {allGoalsZero ? (
        <Typography variant="caption" color="text.secondary">
          Set daily/weekly/monthly goals in Profile & goals to track progress here.
        </Typography>
      ) : (
        <Stack spacing={1.25}>
          <Bar
            label="Today — bids applied"
            value={d.progress.daily_bids.value}
            target={d.progress.daily_bids.target}
            active={activeKinds.has('daily_bids')}
          />
          <Bar
            label="Last 7 days — interviews booked"
            value={d.progress.weekly_interviews.value}
            target={d.progress.weekly_interviews.target}
            active={activeKinds.has('weekly_interviews')}
          />
          <Bar
            label="This month — offers"
            value={d.progress.monthly_offers.value}
            target={d.progress.monthly_offers.target}
            active={activeKinds.has('monthly_offers')}
          />
        </Stack>
      )}
    </Paper>
  );
}
