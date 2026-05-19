import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { useTheme } from '@mui/material/styles';
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Box,
  Button,
  LinearProgress,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
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
import {
  defaultIsoWeekFieldValue,
  defaultMonthFieldValue,
  formatInterviewRangeCaption,
  interviewBoundsFromIsoWeekField,
  interviewBoundsFromMonthField,
  type InterviewRangeMode,
} from '../utils/interviewWindow';
import {
  computeOverviewScore,
  DEFAULT_OVERVIEW_WEIGHTS,
  mergeOverviewWeightsPartial,
  OVERVIEW_WEIGHT_FIELD_META,
  type OverviewScoreWeights,
} from '../utils/overviewScore';
import { LeaderboardProgressLine } from '../components/LeaderboardProgressLine';
import { OverviewChart } from '../components/OverviewChart';

type GroupMeOverview = {
  group: {
    _id: string;
    overviewScoreWeights?: Partial<OverviewScoreWeights> | null;
  };
  role: 'creator' | 'member' | 'none';
};

/**
 * Columns shown in the score table. Trimmed per product spec: applied, phone_screening, interview,
 * offer — the stages members actually care about. Legacy 'screening' counts roll into
 * phone_screening at render time.
 */
const BID_STATUS_ORDER = ['applied', 'phone_screening', 'interview', 'offer'] as const;

type InterviewBucketCounts = { total: number; passed: number; failed: number };

type OverviewRow = {
  user: { id: string; nickname: string; email: string };
  linksCreated: number;
  bidsCreatedInRange: number;
  bidsTouchedInRange: number;
  byStatus: Record<string, number>;
  interviewsInRange: number;
  interviewsPassed: number;
  interviewsFailed: number;
  interviewPassRate: number | null;
  assessmentsInRange: number;
  assessmentsPassed: number;
  assessmentsFailed: number;
  assessmentPassRate: number | null;
  /** Per-bucket interview outcome counts: phone_screening, interview, assessment, offer. */
  byInterviewType?: {
    phone_screening: InterviewBucketCounts;
    interview: InterviewBucketCounts;
    assessment: InterviewBucketCounts;
    offer: InterviewBucketCounts;
  };
};

type ScoredRow = OverviewRow & { score: number };

export default function OverviewPage() {
  const theme = useTheme();
  const qc = useQueryClient();
  const { groupId } = useParams();
  const [rangeMode, setRangeMode] = useState<InterviewRangeMode>('week');
  const [weekField, setWeekField] = useState(() => defaultIsoWeekFieldValue());
  const [monthField, setMonthField] = useState(() => defaultMonthFieldValue());
  const [ownerWeights, setOwnerWeights] = useState<OverviewScoreWeights>(DEFAULT_OVERVIEW_WEIGHTS);
  const ownerSyncedForGroupRef = useRef<string | null>(null);
  const lastSavedJson = useRef<string | null>(null);

  const meQ = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () => (await api.get(`/groups/${groupId}/me`)).data as GroupMeOverview,
  });

  const isOwner = meQ.data?.role === 'creator';

  const patchWeights = useMutation({
    mutationFn: async (w: OverviewScoreWeights) => {
      await api.patch(`/groups/${groupId}/overview-score-weights`, w);
    },
    onSuccess: async (_, w) => {
      lastSavedJson.current = JSON.stringify(w);
      qc.setQueryData(['group', groupId, 'me'], (prev: unknown) => {
        if (!prev || typeof prev !== 'object') return prev;
        const o = prev as GroupMeOverview;
        return {
          ...o,
          group: { ...o.group, overviewScoreWeights: w },
        };
      });
    },
  });

  useEffect(() => {
    ownerSyncedForGroupRef.current = null;
    lastSavedJson.current = null;
  }, [groupId]);

  useEffect(() => {
    if (!meQ.isSuccess || !isOwner || !groupId) return;
    if (ownerSyncedForGroupRef.current === groupId) return;
    const m = mergeOverviewWeightsPartial(meQ.data.group?.overviewScoreWeights);
    setOwnerWeights(m);
    lastSavedJson.current = JSON.stringify(m);
    ownerSyncedForGroupRef.current = groupId;
  }, [meQ.isSuccess, isOwner, groupId, meQ.data]);

  const weights = useMemo(() => {
    if (!meQ.isSuccess || !meQ.data) return DEFAULT_OVERVIEW_WEIGHTS;
    if (!isOwner) return mergeOverviewWeightsPartial(meQ.data.group?.overviewScoreWeights);
    return ownerWeights;
  }, [meQ.isSuccess, meQ.data, isOwner, ownerWeights]);

  useEffect(() => {
    if (!groupId || !isOwner || ownerSyncedForGroupRef.current !== groupId) return;
    const serialized = JSON.stringify(ownerWeights);
    if (lastSavedJson.current === serialized) return;
    const t = window.setTimeout(() => {
      patchWeights.mutate(ownerWeights);
    }, 600);
    return () => window.clearTimeout(t);
  }, [ownerWeights, groupId, isOwner, patchWeights]);

  const bounds = useMemo(() => {
    return rangeMode === 'week'
      ? interviewBoundsFromIsoWeekField(weekField)
      : interviewBoundsFromMonthField(monthField);
  }, [rangeMode, weekField, monthField]);

  const rangeKey = rangeMode === 'week' ? weekField : monthField;

  const q = useQuery({
    queryKey: ['overview-bids', groupId, rangeMode, rangeKey, bounds.from, bounds.to] as const,
    enabled: !!groupId && meQ.isSuccess,
    queryFn: async ({ queryKey }) => {
      const [, gid, , , from, to] = queryKey;
      const { data } = await api.get(`/groups/${gid}/overview/bids`, {
        params: { from, to },
      });
      return data as { from: string; to: string; summary: OverviewRow[] };
    },
  });

  const scored = useMemo((): ScoredRow[] => {
    const rows = q.data?.summary ?? [];
    return rows
      .map((row) => ({
        ...row,
        score: computeOverviewScore(
          {
            linksCreated: row.linksCreated,
            bidsCreatedInRange: row.bidsCreatedInRange,
            bidsTouchedInRange: row.bidsTouchedInRange,
            byStatus: row.byStatus,
            interviewsInRange: row.interviewsInRange,
            interviewsPassed: row.interviewsPassed,
            interviewsFailed: row.interviewsFailed,
            interviewPassRate: row.interviewPassRate,
            assessmentsInRange: row.assessmentsInRange,
            assessmentsPassed: row.assessmentsPassed,
            assessmentsFailed: row.assessmentsFailed,
            assessmentPassRate: row.assessmentPassRate,
          },
          weights
        ),
      }))
      .sort((a, b) => b.score - a.score || (a.user.nickname || '').localeCompare(b.user.nickname || ''));
  }, [q.data?.summary, weights]);

  const interviewStackData = useMemo(
    () =>
      scored.map((r) => ({
        name: r.user.nickname || '—',
        passed: r.interviewsPassed,
        failed: r.interviewsFailed,
      })),
    [scored]
  );

  if (!groupId) return null;

  return (
    <Stack spacing={2}>
      <Typography variant="h5">Group bid overview</Typography>

      {groupId && <LeaderboardProgressLine groupId={groupId} />}
      {groupId && <OverviewChart groupId={groupId} />}

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
          {formatInterviewRangeCaption(rangeMode, weekField, monthField)} · table ranked by weighted score; weights below
        </Typography>
      </Stack>

      <Typography color="text.secondary" variant="body2">
        Bid status counts and interview outcomes for the selected window. Legacy
        <code> screening </code> rows are folded into <strong>Phone screening</strong>. Use score
        weights (below the table) to tune ranking.
      </Typography>

      {(meQ.isLoading || q.isLoading) && <LinearProgress />}

      <Accordion defaultExpanded={false} variant="outlined">
        <AccordionSummary expandIcon={<ExpandMoreIcon />}>
          <Typography fontWeight={600}>Score table</Typography>
          <Typography variant="caption" color="text.secondary" sx={{ ml: 2 }}>
            Per-member counts + interview outcomes for the selected window.
          </Typography>
        </AccordionSummary>
        <AccordionDetails sx={{ p: 0 }}>
      <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
        <Table size="small" stickyHeader>
          <TableHead>
            <TableRow>
              <TableCell rowSpan={2}>Member</TableCell>
              <TableCell rowSpan={2} align="right">
                Score
              </TableCell>
              {BID_STATUS_ORDER.map((s) => (
                <TableCell key={s} rowSpan={2} align="right">
                  {s === 'phone_screening' ? 'Phone screening' : s.charAt(0).toUpperCase() + s.slice(1)}
                </TableCell>
              ))}
              <TableCell colSpan={2} align="center">
                Phone screening
              </TableCell>
              <TableCell colSpan={2} align="center">
                Interview
              </TableCell>
              <TableCell colSpan={2} align="center">
                Assessment
              </TableCell>
              <TableCell colSpan={2} align="center">
                Offer
              </TableCell>
            </TableRow>
            <TableRow>
              <TableCell align="right">Pass</TableCell>
              <TableCell align="right">Fail</TableCell>
              <TableCell align="right">Pass</TableCell>
              <TableCell align="right">Fail</TableCell>
              <TableCell align="right">Pass</TableCell>
              <TableCell align="right">Fail</TableCell>
              <TableCell align="right">Pass</TableCell>
              <TableCell align="right">Fail</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {scored.map((row) => {
              const empty = { total: 0, passed: 0, failed: 0 };
              const bit = row.byInterviewType || {
                phone_screening: empty,
                interview: empty,
                assessment: empty,
                offer: empty,
              };
              return (
                <TableRow key={row.user.id} hover>
                  <TableCell>{row.user.nickname}</TableCell>
                  <TableCell align="right" sx={{ fontWeight: 700 }}>
                    {Math.round(row.score * 100) / 100}
                  </TableCell>
                  {BID_STATUS_ORDER.map((s) => {
                    const count =
                      s === 'phone_screening'
                        ? (row.byStatus.phone_screening ?? 0) + (row.byStatus.screening ?? 0)
                        : (row.byStatus[s] ?? 0);
                    return (
                      <TableCell key={s} align="right">
                        {count}
                      </TableCell>
                    );
                  })}
                  <TableCell align="right">{bit.phone_screening.passed}</TableCell>
                  <TableCell align="right">{bit.phone_screening.failed}</TableCell>
                  <TableCell align="right">{bit.interview.passed}</TableCell>
                  <TableCell align="right">{bit.interview.failed}</TableCell>
                  <TableCell align="right">{bit.assessment.passed}</TableCell>
                  <TableCell align="right">{bit.assessment.failed}</TableCell>
                  <TableCell align="right">{bit.offer.passed}</TableCell>
                  <TableCell align="right">{bit.offer.failed}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </TableContainer>
        </AccordionDetails>
      </Accordion>

      <Accordion defaultExpanded={false} variant="outlined">
        <AccordionSummary expandIcon={<ExpandMoreIcon />}>
          <Typography fontWeight={600}>Score weights</Typography>
          <Typography variant="caption" color="text.secondary" sx={{ ml: 2 }}>
            Linear score = Σ (weight × metric).
            {isOwner ? ' Saved for this group (all members see the same ranking).' : ' Only the group owner can edit these.'}
          </Typography>
        </AccordionSummary>
        <AccordionDetails>
          <Stack spacing={2}>
            <Box
              sx={{
                display: 'grid',
                gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', md: 'repeat(3, 1fr)' },
                gap: 1.5,
              }}
            >
              {OVERVIEW_WEIGHT_FIELD_META.map(({ key, label }) => (
                <TextField
                  key={key}
                  size="small"
                  type="number"
                  label={label}
                  value={weights[key]}
                  disabled={!isOwner}
                  onChange={(e) => {
                    const n = parseFloat(e.target.value);
                    setOwnerWeights((w) => ({ ...w, [key]: Number.isFinite(n) ? n : 0 }));
                  }}
                  inputProps={{ step: 0.5 }}
                />
              ))}
            </Box>
            <Button
              size="small"
              variant="outlined"
              disabled={!isOwner}
              onClick={() => setOwnerWeights({ ...DEFAULT_OVERVIEW_WEIGHTS })}
              sx={{ alignSelf: 'flex-start' }}
            >
              Reset defaults
            </Button>
          </Stack>
        </AccordionDetails>
      </Accordion>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle2" gutterBottom>
          Interview pass vs fail (by member)
        </Typography>
        <Box sx={{ width: '100%', height: 280 }}>
          <ResponsiveContainer>
            <BarChart data={interviewStackData} margin={{ top: 8, right: 8, left: 0, bottom: 48 }}>
              <CartesianGrid strokeDasharray="3 3" stroke={theme.palette.divider} />
              <XAxis dataKey="name" tick={{ fontSize: 11 }} interval={0} angle={-28} textAnchor="end" height={60} />
              <YAxis tick={{ fontSize: 11 }} />
              <Legend />
              <RechartsTooltip />
              <Bar dataKey="passed" name="Passed" stackId="iv" fill="#26a69a" />
              <Bar dataKey="failed" name="Failed" stackId="iv" fill="#ef5350" />
            </BarChart>
          </ResponsiveContainer>
        </Box>
      </Paper>
    </Stack>
  );
}
