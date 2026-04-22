import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { useTheme } from '@mui/material/styles';
import {
  Box,
  Checkbox,
  FormControlLabel,
  FormGroup,
  LinearProgress,
  Paper,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Legend,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip as RechartsTooltip,
  XAxis,
  YAxis,
} from 'recharts';
import api from '../api/client';
import {
  defaultIsoWeekFieldValue,
  defaultMonthFieldValue,
  formatInterviewRangeCaption,
  interviewBoundsFromIsoWeekField,
  interviewBoundsFromMonthField,
  type InterviewRangeMode,
} from '../utils/interviewWindow';

type StatsSummary = {
  from: string;
  to: string;
  links: { created: number };
  bids: {
    createdInRange: number;
    updatedInRange: number;
    byStatus: Record<string, number>;
    offerLike: number;
    negativeLike: number;
  };
  interviews: {
    totalInRange: number;
    byStatus: Record<string, number>;
    byType: { HR: number; TECH: number; ASSESSMENT: number; OTHER: number };
    passed: number;
    failed: number;
    decided: number;
    passRate: number | null;
    failureRate: number | null;
  };
};

function pct(n: number | null | undefined): string {
  if (n == null || Number.isNaN(n)) return '—';
  return `${(n * 100).toFixed(1)}%`;
}

const PIE_COLORS = ['#5c6bc0', '#26a69a', '#ffb74d', '#ef5350', '#42a5f5', '#ab47bc', '#78909c', '#8d6e63'];

export default function StatsPage() {
  const theme = useTheme();
  const { groupId } = useParams();
  const [rangeMode, setRangeMode] = useState<InterviewRangeMode>('week');
  const [weekField, setWeekField] = useState(() => defaultIsoWeekFieldValue());
  const [monthField, setMonthField] = useState(() => defaultMonthFieldValue());
  const [selected, setSelected] = useState<Record<string, boolean>>({});
  const [viewMode, setViewMode] = useState<'table' | 'charts'>('table');

  const bounds = useMemo(() => {
    return rangeMode === 'week'
      ? interviewBoundsFromIsoWeekField(weekField)
      : interviewBoundsFromMonthField(monthField);
  }, [rangeMode, weekField, monthField]);

  const rangeKey = rangeMode === 'week' ? weekField : monthField;

  const { data: members } = useQuery({
    queryKey: ['group-members', groupId],
    enabled: !!groupId,
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/members`)).data as {
        users: { _id: string; nickname: string; email: string }[];
      },
  });

  const userIdsParam = useMemo(() => {
    const ids = Object.entries(selected)
      .filter(([, v]) => v)
      .map(([k]) => k);
    return ids.length ? ids.join(',') : undefined;
  }, [selected]);

  const summary = useQuery({
    queryKey: ['stats-summary', groupId, rangeMode, rangeKey, bounds.from, bounds.to, userIdsParam] as const,
    enabled: !!groupId,
    queryFn: async ({ queryKey }) => {
      const [, gid, , , from, to, userIds] = queryKey;
      const { data } = await api.get(`/groups/${gid}/stats/summary`, {
        params: { from, to, userIds },
      });
      return data as StatsSummary;
    },
  });

  const iv = summary.data?.interviews;
  const ivTypeTotal =
    (iv?.byType.HR ?? 0) +
    (iv?.byType.TECH ?? 0) +
    (iv?.byType.ASSESSMENT ?? 0) +
    (iv?.byType.OTHER ?? 0);

  const bidStatusBarData = useMemo(
    () =>
      Object.entries(summary.data?.bids.byStatus ?? {})
        .filter(([, v]) => v > 0)
        .map(([name, value]) => ({ name, value })),
    [summary.data?.bids.byStatus]
  );

  const ivTypePieData = useMemo(() => {
    const t = summary.data?.interviews.byType;
    if (!t) return [];
    return [
      { name: 'HR', value: t.HR },
      { name: 'Tech', value: t.TECH },
      { name: 'Assessment', value: t.ASSESSMENT },
      { name: 'Other', value: t.OTHER },
    ].filter((x) => x.value > 0);
  }, [summary.data?.interviews.byType]);

  const ivStatusBarData = useMemo(
    () =>
      Object.entries(summary.data?.interviews.byStatus ?? {}).map(([name, value]) => ({
        name,
        value,
      })),
    [summary.data?.interviews.byStatus]
  );

  const activityFunnelData = useMemo(() => {
    const d = summary.data;
    if (!d) return [];
    return [
      { name: 'Links', count: d.links.created },
      { name: 'New bids', count: d.bids.createdInRange },
      { name: 'Bids touched', count: d.bids.updatedInRange },
      { name: 'Interviews', count: d.interviews.totalInRange },
    ];
  }, [summary.data]);

  const outcomePieData = useMemo(() => {
    const p = summary.data?.interviews.passed ?? 0;
    const f = summary.data?.interviews.failed ?? 0;
    if (p === 0 && f === 0) return [];
    return [
      { name: 'Passed', value: p },
      { name: 'Failed', value: f },
    ];
  }, [summary.data?.interviews.passed, summary.data?.interviews.failed]);

  const primary = theme.palette.primary.main;
  const secondary = theme.palette.secondary.main;

  if (!groupId) return null;

  return (
    <Stack spacing={2}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} alignItems={{ sm: 'center' }} flexWrap="wrap" useFlexGap>
        <Typography variant="h5">Statistics</Typography>
        <ToggleButtonGroup
          value={viewMode}
          exclusive
          size="small"
          onChange={(_, v: 'table' | 'charts' | null) => {
            if (v) setViewMode(v);
          }}
          aria-label="Table or chart view"
        >
          <ToggleButton value="table">Table</ToggleButton>
          <ToggleButton value="charts">Charts</ToggleButton>
        </ToggleButtonGroup>
      </Stack>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle2" gutterBottom>
          Period
        </Typography>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} alignItems={{ sm: 'center' }} flexWrap="wrap" useFlexGap>
          <ToggleButtonGroup
            value={rangeMode}
            exclusive
            size="small"
            onChange={(_, v: InterviewRangeMode | null) => {
              if (v) setRangeMode(v);
            }}
            aria-label="Week or month range"
          >
            <ToggleButton value="week">Week</ToggleButton>
            <ToggleButton value="month">Month</ToggleButton>
          </ToggleButtonGroup>
          {rangeMode === 'week' ? (
            <TextField
              type="week"
              size="small"
              value={weekField}
              onChange={(e) => setWeekField(e.target.value)}
              inputProps={{ 'aria-label': 'Calendar week' }}
              sx={{
                maxWidth: 160,
                '& .MuiInputBase-input': { py: 0.5, fontSize: '0.8125rem' },
              }}
            />
          ) : (
            <TextField
              type="month"
              size="small"
              value={monthField}
              onChange={(e) => setMonthField(e.target.value)}
              inputProps={{ 'aria-label': 'Calendar month' }}
              sx={{
                maxWidth: 148,
                '& .MuiInputBase-input': { py: 0.5, fontSize: '0.8125rem' },
              }}
            />
          )}
          <Typography variant="caption" color="text.secondary">
            {formatInterviewRangeCaption(rangeMode, weekField, monthField)} · same half-open window as the interview panel
            (scheduled date in range, or outcome logged in range)
          </Typography>
        </Stack>

        <Typography variant="caption" color="text.secondary" sx={{ mt: 2, display: 'block' }}>
          Select members to include (empty = whole group). Applies to links (creator), bids, and interviews.
        </Typography>
        <FormGroup row sx={{ mt: 1 }}>
          {members?.users?.map((u) => (
            <FormControlLabel
              key={u._id}
              control={
                <Checkbox
                  checked={!!selected[u._id]}
                  onChange={(_, c) => setSelected((s) => ({ ...s, [u._id]: c }))}
                />
              }
              label={u.nickname}
            />
          ))}
        </FormGroup>
      </Paper>

      {summary.isLoading && <LinearProgress />}

      {viewMode === 'charts' ? (
        <Stack spacing={2}>
          <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2}>
            <Paper variant="outlined" sx={{ p: 2, flex: 1, minWidth: 0 }}>
              <Typography variant="subtitle2" gutterBottom>
                Activity funnel (filtered)
              </Typography>
              <Box sx={{ width: '100%', height: 260 }}>
                <ResponsiveContainer>
                  <BarChart data={activityFunnelData} margin={{ top: 8, right: 8, left: 0, bottom: 8 }}>
                    <CartesianGrid strokeDasharray="3 3" stroke={theme.palette.divider} />
                    <XAxis dataKey="name" tick={{ fontSize: 11 }} />
                    <YAxis tick={{ fontSize: 11 }} allowDecimals={false} />
                    <RechartsTooltip />
                    <Bar dataKey="count" name="Count" fill={primary} radius={[4, 4, 0, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </Box>
            </Paper>
            <Paper variant="outlined" sx={{ p: 2, flex: 1, minWidth: 0 }}>
              <Typography variant="subtitle2" gutterBottom>
                Bid outcomes (offer-like vs negative, updated rows)
              </Typography>
              <Box sx={{ width: '100%', height: 260 }}>
                <ResponsiveContainer>
                  <BarChart
                    data={[
                      {
                        name: 'Signals',
                        positive: summary.data?.bids.offerLike ?? 0,
                        negative: summary.data?.bids.negativeLike ?? 0,
                      },
                    ]}
                    margin={{ top: 8, right: 8, left: 0, bottom: 8 }}
                  >
                    <CartesianGrid strokeDasharray="3 3" stroke={theme.palette.divider} />
                    <XAxis dataKey="name" tick={{ fontSize: 11 }} />
                    <YAxis tick={{ fontSize: 11 }} allowDecimals={false} />
                    <Legend />
                    <RechartsTooltip />
                    <Bar dataKey="positive" name="Offer / accepted" fill="#26a69a" />
                    <Bar dataKey="negative" name="Rejected / withdrawn" fill="#ef5350" />
                  </BarChart>
                </ResponsiveContainer>
              </Box>
            </Paper>
          </Stack>

          <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2}>
            <Paper variant="outlined" sx={{ p: 2, flex: 1, minWidth: 0 }}>
              <Typography variant="subtitle2" gutterBottom>
                Bid status mix (rows updated in period)
              </Typography>
              <Box sx={{ width: '100%', height: 280 }}>
                {bidStatusBarData.length === 0 ? (
                  <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                    No bid updates in this window.
                  </Typography>
                ) : (
                  <ResponsiveContainer>
                    <BarChart data={bidStatusBarData} layout="vertical" margin={{ top: 8, right: 16, left: 72, bottom: 8 }}>
                      <CartesianGrid strokeDasharray="3 3" stroke={theme.palette.divider} />
                      <XAxis type="number" allowDecimals={false} />
                      <YAxis type="category" dataKey="name" width={68} tick={{ fontSize: 11 }} />
                      <RechartsTooltip />
                      <Bar dataKey="value" name="Bids" fill={secondary} radius={[0, 4, 4, 0]} />
                    </BarChart>
                  </ResponsiveContainer>
                )}
              </Box>
            </Paper>
            <Paper variant="outlined" sx={{ p: 2, flex: 1, minWidth: 0 }}>
              <Typography variant="subtitle2" gutterBottom>
                Interview types
              </Typography>
              <Box sx={{ width: '100%', height: 280 }}>
                {ivTypePieData.length === 0 ? (
                  <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                    No interviews in this window.
                  </Typography>
                ) : (
                  <ResponsiveContainer>
                    <PieChart>
                      <Pie
                        data={ivTypePieData}
                        dataKey="value"
                        nameKey="name"
                        cx="50%"
                        cy="50%"
                        outerRadius={90}
                        label={({ name, percent }) => `${name} ${(percent * 100).toFixed(0)}%`}
                      >
                        {ivTypePieData.map((_, i) => (
                          <Cell key={i} fill={PIE_COLORS[i % PIE_COLORS.length]} />
                        ))}
                      </Pie>
                      <RechartsTooltip />
                    </PieChart>
                  </ResponsiveContainer>
                )}
              </Box>
            </Paper>
          </Stack>

          <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2}>
            <Paper variant="outlined" sx={{ p: 2, flex: 1, minWidth: 0 }}>
              <Typography variant="subtitle2" gutterBottom>
                Interview status
              </Typography>
              <Box sx={{ width: '100%', height: 280 }}>
                {ivStatusBarData.length === 0 ? (
                  <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                    No interviews in this window.
                  </Typography>
                ) : (
                  <ResponsiveContainer>
                    <BarChart data={ivStatusBarData} margin={{ top: 8, right: 8, left: 0, bottom: 40 }}>
                      <CartesianGrid strokeDasharray="3 3" stroke={theme.palette.divider} />
                      <XAxis dataKey="name" tick={{ fontSize: 10 }} interval={0} angle={-20} textAnchor="end" height={52} />
                      <YAxis tick={{ fontSize: 11 }} allowDecimals={false} />
                      <RechartsTooltip />
                      <Bar dataKey="value" name="Count" fill={primary} radius={[4, 4, 0, 0]} />
                    </BarChart>
                  </ResponsiveContainer>
                )}
              </Box>
            </Paper>
            <Paper variant="outlined" sx={{ p: 2, flex: 1, minWidth: 0 }}>
              <Typography variant="subtitle2" gutterBottom>
                Passed vs failed interviews
              </Typography>
              <Box sx={{ width: '100%', height: 280 }}>
                {outcomePieData.length === 0 ? (
                  <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                    No passed/failed outcomes in this window.
                  </Typography>
                ) : (
                  <ResponsiveContainer>
                    <PieChart>
                      <Pie
                        data={outcomePieData}
                        dataKey="value"
                        nameKey="name"
                        cx="50%"
                        cy="50%"
                        innerRadius={48}
                        outerRadius={88}
                        label={({ name, value }) => `${name}: ${value}`}
                      >
                        <Cell fill="#26a69a" />
                        <Cell fill="#ef5350" />
                      </Pie>
                      <RechartsTooltip />
                      <text
                        x="50%"
                        y="50%"
                        textAnchor="middle"
                        dominantBaseline="middle"
                        fill={theme.palette.text.secondary}
                        fontSize={12}
                      >
                        Pass {pct(iv?.passRate ?? null)}
                      </text>
                    </PieChart>
                  </ResponsiveContainer>
                )}
              </Box>
            </Paper>
          </Stack>
        </Stack>
      ) : (
        <>
          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
            <Paper variant="outlined" sx={{ p: 2, flex: 1 }}>
              <Typography variant="subtitle1" gutterBottom>
                Links shared
              </Typography>
              <Typography variant="body2" color="text.secondary">
                New group job links created in this period.
              </Typography>
              <Typography variant="h4" sx={{ mt: 2 }}>
                {summary.data?.links.created ?? '—'}
              </Typography>
            </Paper>

            <Paper variant="outlined" sx={{ p: 2, flex: 1 }}>
              <Typography variant="subtitle1" gutterBottom>
                Bids
              </Typography>
              <Typography variant="body2" color="text.secondary">
                New bid rows vs rows with any update in this period (status mix counts the latter).
              </Typography>
              <Stack spacing={1} sx={{ mt: 2 }}>
                <Stack direction="row" justifyContent="space-between">
                  <Typography variant="body2">Created in period</Typography>
                  <Typography variant="body2">{summary.data?.bids.createdInRange ?? '—'}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography variant="body2">Updated in period</Typography>
                  <Typography variant="body2">{summary.data?.bids.updatedInRange ?? '—'}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography variant="body2">Offer / accepted (in updated set)</Typography>
                  <Typography variant="body2">{summary.data?.bids.offerLike ?? '—'}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography variant="body2">Rejected / withdrawn (in updated set)</Typography>
                  <Typography variant="body2">{summary.data?.bids.negativeLike ?? '—'}</Typography>
                </Stack>
              </Stack>
            </Paper>
          </Stack>

          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="subtitle1" gutterBottom>
              Bid status (rows updated in period)
            </Typography>
            <Stack spacing={1} sx={{ mt: 1 }}>
              {Object.keys(summary.data?.bids.byStatus ?? {}).length === 0 && (
                <Typography variant="body2" color="text.secondary">
                  No bid updates in this window.
                </Typography>
              )}
              {Object.entries(summary.data?.bids.byStatus ?? {}).map(([k, v]) => (
                <Stack key={k} direction="row" justifyContent="space-between">
                  <Typography variant="body2">{k}</Typography>
                  <Typography variant="body2">{v}</Typography>
                </Stack>
              ))}
            </Stack>
          </Paper>

          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
            <Paper variant="outlined" sx={{ p: 2, flex: 1 }}>
              <Typography variant="subtitle1" gutterBottom>
                Interviews
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Total rows matching the interview panel time rules: {iv?.totalInRange ?? '—'}
              </Typography>
              <Stack spacing={1} sx={{ mt: 2 }}>
                <Stack direction="row" justifyContent="space-between">
                  <Typography>HR</Typography>
                  <Typography>{iv?.byType.HR ?? 0}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography>Tech (1–3)</Typography>
                  <Typography>{iv?.byType.TECH ?? 0}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography>Assessment (take-home / async)</Typography>
                  <Typography>{iv?.byType.ASSESSMENT ?? 0}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography>Client / offer / other types</Typography>
                  <Typography>{iv?.byType.OTHER ?? 0}</Typography>
                </Stack>
              </Stack>
              <Box sx={{ mt: 2, height: 8, bgcolor: 'divider', borderRadius: 1, overflow: 'hidden', display: 'flex' }}>
                <Box
                  sx={{
                    width: `${((iv?.byType.HR ?? 0) / Math.max(1, ivTypeTotal)) * 100}%`,
                    bgcolor: 'primary.main',
                  }}
                />
                <Box
                  sx={{
                    width: `${((iv?.byType.TECH ?? 0) / Math.max(1, ivTypeTotal)) * 100}%`,
                    bgcolor: 'secondary.main',
                  }}
                />
                <Box
                  sx={{
                    width: `${((iv?.byType.ASSESSMENT ?? 0) / Math.max(1, ivTypeTotal)) * 100}%`,
                    bgcolor: 'warning.light',
                  }}
                />
                <Box
                  sx={{
                    width: `${((iv?.byType.OTHER ?? 0) / Math.max(1, ivTypeTotal)) * 100}%`,
                    bgcolor: 'action.disabledBackground',
                  }}
                />
              </Box>
            </Paper>

            <Paper variant="outlined" sx={{ p: 2, flex: 1 }}>
              <Typography variant="subtitle1" gutterBottom>
                Outcomes
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Pass and fail rates use interviews marked passed or failed in this window.
              </Typography>
              <Stack spacing={1} sx={{ mt: 2 }}>
                <Stack direction="row" justifyContent="space-between">
                  <Typography>Passed</Typography>
                  <Typography>{iv?.passed ?? 0}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography>Failed</Typography>
                  <Typography>{iv?.failed ?? 0}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography>Pass rate</Typography>
                  <Typography>{pct(iv?.passRate ?? null)}</Typography>
                </Stack>
                <Stack direction="row" justifyContent="space-between">
                  <Typography>Failure rate</Typography>
                  <Typography>{pct(iv?.failureRate ?? null)}</Typography>
                </Stack>
              </Stack>
            </Paper>
          </Stack>

          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="subtitle1" gutterBottom>
              Interview status (all types)
            </Typography>
            <Stack spacing={1} sx={{ mt: 1 }}>
              {Object.keys(iv?.byStatus ?? {}).length === 0 && (
                <Typography variant="body2" color="text.secondary">
                  No interviews in this window.
                </Typography>
              )}
              {Object.entries(iv?.byStatus ?? {}).map(([k, v]) => (
                <Stack key={k} direction="row" justifyContent="space-between">
                  <Typography variant="body2">{k}</Typography>
                  <Typography variant="body2">{v}</Typography>
                </Stack>
              ))}
            </Stack>
          </Paper>
        </>
      )}
    </Stack>
  );
}
