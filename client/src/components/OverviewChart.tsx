import { useEffect, useMemo, useState } from 'react';
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
import { getMyProfile } from '../api/profile';
import {
  CartesianGrid,
  Legend,
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
  { key: 'applied', label: 'Applied bids' },
  { key: 'interviews_from_bidders', label: 'Interviews from bidders' },
  { key: 'interviews_from_callers', label: 'Interviews from callers' },
  { key: 'pass_rate_from_callers', label: 'Pass rate from callers', isRate: true },
  { key: 'catch_rate_from_bidders', label: 'Catch rate from bidders', isRate: true },
];

type UserSeries = {
  userId: string;
  nickname: string;
  points: Array<{ day: string; value: number }>;
};

type ChartResponse = {
  metric: Metric;
  bucket: 'day' | 'week';
  from: string;
  to: string;
  buckets: string[];
  series: UserSeries[];
};

/** Compact selector range: UTC-12 through UTC+14 in 1-hour steps, with a special UTC entry. */
const OFFSET_HOUR_OPTIONS = (() => {
  const out: Array<{ value: number; label: string }> = [];
  for (let h = -12; h <= 14; h += 1) {
    if (h === 0) {
      out.push({ value: 0, label: 'UTC' });
    } else {
      const sign = h > 0 ? '+' : '-';
      out.push({ value: h, label: `UTC${sign}${Math.abs(h)}` });
    }
  }
  return out;
})();

/** Resolve an IANA timezone to its current UTC offset (minutes). DST-aware via Intl. */
function ianaToOffsetMinutes(tz: string | undefined | null): number {
  if (!tz) return 0;
  try {
    const now = new Date();
    const local = new Date(now.toLocaleString('en-US', { timeZone: tz }));
    const utc = new Date(now.toLocaleString('en-US', { timeZone: 'UTC' }));
    return Math.round((local.getTime() - utc.getTime()) / 60000);
  } catch {
    return 0;
  }
}

/** Stable color palette for lines. Cycles if there are more users than colors. */
const SERIES_COLORS = [
  '#1976d2',
  '#26a69a',
  '#ef5350',
  '#ab47bc',
  '#ffa726',
  '#42a5f5',
  '#66bb6a',
  '#ec407a',
  '#8d6e63',
  '#78909c',
  '#5c6bc0',
  '#d4e157',
];

type Props = { groupId: string };

export function OverviewChart({ groupId }: Props) {
  const theme = useTheme();
  const [metric, setMetric] = useState<Metric>('applied');
  const [tzOffsetHours, setTzOffsetHours] = useState<number>(0);

  /** Default the offset from the user's profile timezone the first time their profile loads. */
  const profileQ = useQuery({
    queryKey: ['profile', 'me'] as const,
    queryFn: getMyProfile,
    staleTime: 5 * 60 * 1000,
  });
  const profileTz = profileQ.data?.timezone;
  useEffect(() => {
    if (!profileTz) return;
    const minutes = ianaToOffsetMinutes(profileTz);
    /** Hour-granularity selector; snap minutes-aware zones (India +5:30) to nearest hour. */
    setTzOffsetHours(Math.round(minutes / 60));
    /** Only seed once, on profile load. */
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [profileTz]);

  const q = useQuery({
    queryKey: ['overview-chart', groupId, metric, tzOffsetHours] as const,
    enabled: !!groupId,
    queryFn: async () =>
      (
        await api.get(`/groups/${groupId}/overview/chart`, {
          params: { metric, tzOffsetMinutes: tzOffsetHours * 60 },
        })
      ).data as ChartResponse,
    refetchOnWindowFocus: true,
    staleTime: 30_000,
  });

  const metricMeta = METRICS.find((m) => m.key === metric)!;

  /**
   * Pivot per-user series into recharts' row-per-bucket shape:
   * [{ day: '05-13', [userId1]: v, [userId2]: v, ... }, ...]
   * Values are converted to % for rate metrics so axes look right.
   */
  const { data, series } = useMemo(() => {
    const buckets = q.data?.buckets ?? [];
    const allSeries = q.data?.series ?? [];
    const rows = buckets.map((day) => {
      const row: Record<string, string | number> = { day: day.slice(5) };
      for (const s of allSeries) {
        const point = s.points.find((p) => p.day === day);
        const raw = point?.value ?? 0;
        row[s.userId] = metricMeta.isRate ? Math.round(raw * 1000) / 10 : raw;
      }
      return row;
    });
    return { data: rows, series: allSeries };
  }, [q.data, metricMeta.isRate]);

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
        <Stack direction="row" spacing={1}>
          <TextField
            select
            size="small"
            value={metric}
            onChange={(e) => setMetric(e.target.value as Metric)}
            sx={{ minWidth: 260 }}
          >
            {METRICS.map((m) => (
              <MenuItem key={m.key} value={m.key}>
                {m.label}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            select
            size="small"
            label="Day boundary"
            value={tzOffsetHours}
            onChange={(e) => setTzOffsetHours(Number(e.target.value))}
            sx={{ minWidth: 120 }}
          >
            {OFFSET_HOUR_OPTIONS.map((o) => (
              <MenuItem key={o.value} value={o.value}>
                {o.label}
              </MenuItem>
            ))}
          </TextField>
        </Stack>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Last 7 days bucketed by{' '}
        {tzOffsetHours === 0
          ? 'UTC midnight'
          : `UTC${tzOffsetHours > 0 ? '+' : '-'}${Math.abs(tzOffsetHours)} midnight`}
        ; one line per user.
        {metricMeta.isRate ? ' Values shown as % for that day.' : ''}
      </Typography>
      {q.isLoading && <LinearProgress sx={{ mb: 1 }} />}
      {q.isError && (
        <Typography variant="caption" color="error.main">
          Could not load chart data.
        </Typography>
      )}
      {!q.isLoading && series.length === 0 && q.data && (
        <Typography variant="caption" color="text.secondary">
          No data for the selected metric in this window.
        </Typography>
      )}
      <Box sx={{ width: '100%', height: 280 }}>
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
            <Legend wrapperStyle={{ fontSize: 12 }} />
            {series.map((s, i) => (
              <Line
                key={s.userId}
                type="monotone"
                dataKey={s.userId}
                name={s.nickname || s.userId.slice(-6)}
                stroke={SERIES_COLORS[i % SERIES_COLORS.length]}
                strokeWidth={2}
                dot={{ r: 3 }}
                activeDot={{ r: 5 }}
                isAnimationActive={false}
              />
            ))}
          </LineChart>
        </ResponsiveContainer>
      </Box>
    </Paper>
  );
}
