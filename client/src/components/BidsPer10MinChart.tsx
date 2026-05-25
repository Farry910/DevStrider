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
  CartesianGrid,
  Legend,
  Line,
  LineChart,
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

const BUCKETS_PER_DAY = 144; // 24 hours × 6 buckets/hour
const BUCKET_MINUTES = 10;

/** YYYY-MM-DD for today in the viewer's local time. */
function todayLocalIso(): string {
  const d = new Date();
  const yyyy = d.getFullYear();
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  return `${yyyy}-${mm}-${dd}`;
}

/** Bucket index 0–143 for a UTC ISO timestamp using the viewer's local hour+minute. */
function bucketIndexOf(iso: string): number {
  const d = new Date(iso);
  return d.getHours() * 6 + Math.floor(d.getMinutes() / BUCKET_MINUTES);
}

/** "HH:MM" label for a bucket index. */
function labelFor(idx: number): string {
  const total = idx * BUCKET_MINUTES;
  const h = Math.floor(total / 60);
  const m = total % 60;
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
}

/** Deterministic colour per user from string hash → HSL hue. */
function colorForUser(userId: string): string {
  let h = 0;
  for (let i = 0; i < userId.length; i++) h = (h * 31 + userId.charCodeAt(i)) | 0;
  return `hsl(${((h % 360) + 360) % 360}, 70%, 50%)`;
}

type Props = {
  groupId: string;
  /**
   * Set of user-ids to display. `null` means "show all". An empty set renders an empty chart
   * with a helpful note. Driven by the shared filter on OverviewPage.
   */
  selectedUserIds: Set<string> | null;
};

/**
 * Bids-per-10-minutes line chart across the chosen subset of users for one calendar day
 * (default: today in the viewer's tz). Server returns raw timestamps; bucketing into 144 ten-
 * minute slots happens here per the project's "heavy frontend, light backend" preference.
 */
export function BidsPer10MinChart({ groupId, selectedUserIds }: Props) {
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
   * Aggregate filtered rows into 144 buckets. Each row in `chartData` is one 10-minute slot
   * keyed by `bucket: 'HH:MM'`, with one numeric field per visible user-id so recharts can
   * render a line per user.
   */
  const { chartData, userOrder, nicknameByUser, total } = useMemo(() => {
    const allRows = q.data?.bids ?? [];
    const rows = selectedUserIds ? allRows.filter((r) => selectedUserIds.has(r.userId)) : allRows;
    const nicknameByUser = new Map<string, string>();
    const userOrder: string[] = [];
    /** Initialize 144 empty buckets so the line keeps a continuous x-axis even on quiet days. */
    const buckets: Array<Record<string, number | string>> = [];
    for (let i = 0; i < BUCKETS_PER_DAY; i++) {
      buckets.push({ bucket: labelFor(i) });
    }
    for (const r of rows) {
      if (!nicknameByUser.has(r.userId)) {
        nicknameByUser.set(r.userId, r.nickname || 'unknown');
        userOrder.push(r.userId);
      }
      const idx = bucketIndexOf(r.ts);
      const slot = buckets[idx];
      slot[r.userId] = ((slot[r.userId] as number | undefined) ?? 0) + 1;
    }
    /** Fill missing user keys with 0 so recharts doesn't break the line for sparse users. */
    for (const slot of buckets) {
      for (const uid of userOrder) {
        if (slot[uid] === undefined) slot[uid] = 0;
      }
    }
    return { chartData: buckets, userOrder, nicknameByUser, total: rows.length };
  }, [q.data, selectedUserIds]);

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
        <Box>
          <Typography variant="subtitle1">Bids per 10 minutes</Typography>
          <Typography variant="caption" color="text.secondary">
            Smoothed line per visible user across 144 ten-minute slots in your local time.
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
        <Stack alignItems="center" justifyContent="center" sx={{ height: 280 }}>
          <CircularProgress size={24} />
        </Stack>
      ) : q.isError ? (
        <Typography variant="body2" color="error">
          Could not load bid timestamps.
        </Typography>
      ) : selectedUserIds && selectedUserIds.size === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>
          No users selected. Pick at least one above to populate the chart.
        </Typography>
      ) : total === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>
          No bids on {date} for the selected users.
        </Typography>
      ) : (
        <Box sx={{ height: 300 }}>
          <ResponsiveContainer>
            <LineChart data={chartData} margin={{ top: 8, right: 16, left: 0, bottom: 4 }}>
              <CartesianGrid strokeDasharray="3 3" />
              <XAxis
                dataKey="bucket"
                /** One tick every 2 hours = every 12 buckets keeps the axis readable. */
                interval={11}
                tick={{ fontSize: 12 }}
              />
              <YAxis allowDecimals={false} tick={{ fontSize: 12 }} />
              <RechartsTooltip
                formatter={(value: number, name: string) => [value, nicknameByUser.get(name) ?? name]}
              />
              <Legend
                wrapperStyle={{ fontSize: 12 }}
                formatter={(uid) => nicknameByUser.get(String(uid)) ?? String(uid)}
              />
              {userOrder.map((uid) => (
                <Line
                  key={uid}
                  type="monotone"
                  dataKey={uid}
                  stroke={colorForUser(uid)}
                  strokeWidth={2}
                  dot={false}
                  activeDot={{ r: 4 }}
                  isAnimationActive={false}
                />
              ))}
            </LineChart>
          </ResponsiveContainer>
        </Box>
      )}
      <Typography variant="caption" color="text.secondary">
        {total} bid{total === 1 ? '' : 's'} from {userOrder.length} user
        {userOrder.length === 1 ? '' : 's'} on {date}.
      </Typography>
    </Paper>
  );
}
