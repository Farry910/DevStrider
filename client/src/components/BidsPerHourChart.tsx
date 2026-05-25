import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Box,
  CircularProgress,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip as RechartsTooltip,
  XAxis,
  YAxis,
} from 'recharts';
import api from '../api/client';

type BidRow = { userId: string; nickname: string; ts: string };
type BidsByDay = {
  date: string;
  tzOffsetMinutes: number;
  from: string;
  to: string;
  bids: BidRow[];
};

/** YYYY-MM-DD for today in the viewer's local time. */
function todayLocalIso(): string {
  const d = new Date();
  const yyyy = d.getFullYear();
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  return `${yyyy}-${mm}-${dd}`;
}

/** Convert a UTC ISO timestamp to the local hour 0–23 in the viewer's tz. */
function localHourOf(iso: string): number {
  return new Date(iso).getHours();
}

/** Deterministic colour per user from string hash → HSL hue. */
function colorForUser(userId: string): string {
  let h = 0;
  for (let i = 0; i < userId.length; i++) h = (h * 31 + userId.charCodeAt(i)) | 0;
  return `hsl(${((h % 360) + 360) % 360}, 70%, 50%)`;
}

type Props = {
  groupId: string;
};

/**
 * Bids-per-hour stacked bar chart across all users for a date (default today). Server returns
 * raw timestamps for that calendar day; bucketing into 24 hourly columns happens here so the
 * endpoint stays a dumb projection.
 */
export function BidsPerHourChart({ groupId }: Props) {
  const [date, setDate] = useState<string>(todayLocalIso);

  const tzOff = useMemo(() => -new Date().getTimezoneOffset(), []);

  const q = useQuery({
    queryKey: ['bidsByDay', groupId, date, tzOff] as const,
    queryFn: async () => {
      const { data } = await api.get<BidsByDay>(
        `/groups/${groupId}/stats/bids-by-day`,
        { params: { date, tzOffsetMinutes: tzOff } }
      );
      return data;
    },
    enabled: Boolean(groupId && date),
    staleTime: 30_000,
  });

  /**
   * Aggregate into per-hour rows keyed by user id. Each row is { hour: '07', <userId>: count, ... }
   * so recharts can stack-by-user automatically.
   */
  const { chartData, userOrder, nicknameByUser, total } = useMemo(() => {
    const rows = q.data?.bids ?? [];
    const userOrder: string[] = [];
    const nicknameByUser = new Map<string, string>();
    const buckets: Record<string, Record<string, number>> = {};
    for (let h = 0; h < 24; h++) {
      buckets[String(h).padStart(2, '0')] = {};
    }
    for (const r of rows) {
      const h = String(localHourOf(r.ts)).padStart(2, '0');
      const uid = r.userId;
      if (!nicknameByUser.has(uid)) {
        nicknameByUser.set(uid, r.nickname || 'unknown');
        userOrder.push(uid);
      }
      buckets[h][uid] = (buckets[h][uid] ?? 0) + 1;
    }
    const chartData = Object.entries(buckets).map(([hour, bag]) => ({ hour, ...bag }));
    return { chartData, userOrder, nicknameByUser, total: rows.length };
  }, [q.data]);

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
        <Box>
          <Typography variant="subtitle1">Bids per hour</Typography>
          <Typography variant="caption" color="text.secondary">
            All non-draft bids in this group for the selected date. Buckets are 1-hour columns in
            your local time.
          </Typography>
        </Box>
        <TextField
          type="date"
          size="small"
          value={date}
          onChange={(e) => setDate(e.target.value)}
          inputProps={{ max: todayLocalIso() }}
          sx={{ minWidth: 160 }}
        />
      </Stack>
      {q.isLoading ? (
        <Stack alignItems="center" justifyContent="center" sx={{ height: 240 }}>
          <CircularProgress size={24} />
        </Stack>
      ) : q.isError ? (
        <Typography variant="body2" color="error">
          Could not load bid timestamps.
        </Typography>
      ) : total === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ py: 4, textAlign: 'center' }}>
          No bids on {date}.
        </Typography>
      ) : (
        <Box sx={{ height: 280 }}>
          <ResponsiveContainer>
            <BarChart data={chartData} margin={{ top: 8, right: 16, left: 0, bottom: 4 }}>
              <CartesianGrid strokeDasharray="3 3" />
              <XAxis dataKey="hour" tickFormatter={(h) => `${h}:00`} />
              <YAxis allowDecimals={false} />
              <RechartsTooltip
                formatter={(value: number, name: string) => [value, nicknameByUser.get(name) ?? name]}
                labelFormatter={(h) => `${h}:00 – ${String((Number(h) + 1) % 24).padStart(2, '0')}:00`}
              />
              <Legend
                formatter={(uid) => nicknameByUser.get(String(uid)) ?? String(uid)}
              />
              {userOrder.map((uid) => (
                <Bar
                  key={uid}
                  dataKey={uid}
                  stackId="bids"
                  fill={colorForUser(uid)}
                  isAnimationActive={false}
                />
              ))}
            </BarChart>
          </ResponsiveContainer>
        </Box>
      )}
      <Typography variant="caption" color="text.secondary">
        {total} total bid{total === 1 ? '' : 's'} on {date}.
      </Typography>
    </Paper>
  );
}
