import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Box,
  LinearProgress,
  MenuItem,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import {
  CartesianGrid,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip as RechartsTooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { useTheme } from '@mui/material/styles';
import api from '../api/client';

type Metric =
  | 'applied'
  | 'interviews_from_bidders'
  | 'interviews_from_callers'
  | 'pass_rate_from_callers'
  | 'catch_rate_from_bidders';

const METRICS: Array<{ key: Metric; label: string; isRate?: boolean }> = [
  { key: 'applied', label: 'Applied bids (daily)' },
  { key: 'interviews_from_bidders', label: 'Interviews from bidders (daily)' },
  { key: 'interviews_from_callers', label: 'Interviews from callers (daily)' },
  { key: 'pass_rate_from_callers', label: 'Pass rate from callers (weekly)', isRate: true },
  { key: 'catch_rate_from_bidders', label: 'Catch rate from bidders (weekly)', isRate: true },
];

type ChartResponse = {
  metric: Metric;
  bucket: 'day' | 'week';
  from: string;
  to: string;
  points: Array<{ day: string; value: number }>;
};

type Props = { groupId: string };

export function OverviewChart({ groupId }: Props) {
  const theme = useTheme();
  const [metric, setMetric] = useState<Metric>('applied');

  const q = useQuery({
    queryKey: ['overview-chart', groupId, metric] as const,
    enabled: !!groupId,
    queryFn: async () =>
      (
        await api.get(`/groups/${groupId}/overview/chart`, {
          params: { metric },
        })
      ).data as ChartResponse,
    refetchOnWindowFocus: true,
    staleTime: 30_000,
  });

  const metricMeta = METRICS.find((m) => m.key === metric)!;
  const data =
    q.data?.points.map((p) => ({
      day: p.day.slice(5), // 'MM-DD'
      value: metricMeta.isRate ? Math.round(p.value * 1000) / 10 : p.value, // percent or raw
    })) ?? [];

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        alignItems={{ sm: 'center' }}
        justifyContent="space-between"
        spacing={1}
        sx={{ mb: 1 }}
      >
        <Typography variant="subtitle1">Weekly trend</Typography>
        <TextField
          select
          size="small"
          value={metric}
          onChange={(e) => setMetric(e.target.value as Metric)}
          sx={{ minWidth: 240 }}
        >
          {METRICS.map((m) => (
            <MenuItem key={m.key} value={m.key}>
              {m.label}
            </MenuItem>
          ))}
        </TextField>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        {metricMeta.isRate
          ? 'Last 8 ISO weeks; each point is that week\'s rate (%).'
          : 'Last 7 UTC days; each point is that day\'s count.'}
      </Typography>
      {q.isLoading && <LinearProgress sx={{ mb: 1 }} />}
      {q.isError && (
        <Typography variant="caption" color="error.main">
          Could not load chart data.
        </Typography>
      )}
      <Box sx={{ width: '100%', height: 240 }}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={data} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke={theme.palette.divider} />
            <XAxis dataKey="day" stroke={theme.palette.text.secondary} tick={{ fontSize: 12 }} />
            <YAxis
              stroke={theme.palette.text.secondary}
              tick={{ fontSize: 12 }}
              tickFormatter={(v) => (metricMeta.isRate ? `${v}%` : String(v))}
              domain={metricMeta.isRate ? [0, 100] : ['auto', 'auto']}
              allowDecimals={false}
            />
            <RechartsTooltip
              formatter={(v: number) => (metricMeta.isRate ? `${v}%` : v)}
              contentStyle={{
                background: theme.palette.background.paper,
                border: `1px solid ${theme.palette.divider}`,
                borderRadius: 4,
              }}
            />
            <Line
              type="monotone"
              dataKey="value"
              stroke={theme.palette.primary.main}
              strokeWidth={2}
              dot={{ r: 3 }}
              activeDot={{ r: 5 }}
              isAnimationActive={false}
            />
          </LineChart>
        </ResponsiveContainer>
      </Box>
    </Paper>
  );
}
