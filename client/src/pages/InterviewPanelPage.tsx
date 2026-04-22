import { useCallback, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import {
  Alert,
  Box,
  LinearProgress,
  Paper,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material';
import api from '../api/client';
import { InterviewStickyHeader } from '../components/interview/InterviewStickyHeader';
import {
  InterviewVirtualBody,
  type InterviewRowType,
} from '../components/interview/InterviewVirtualBody';
import type { InterviewSortField } from '../components/interview/interviewGrid';
import {
  defaultIsoWeekFieldValue,
  defaultMonthFieldValue,
  formatInterviewRangeCaption,
  interviewBoundsFromIsoWeekField,
  interviewBoundsFromMonthField,
  type InterviewRangeMode,
} from '../utils/interviewWindow';

export default function InterviewPanelPage() {
  const { groupId } = useParams();
  const qc = useQueryClient();
  const [scrollEl, setScrollEl] = useState<HTMLDivElement | null>(null);
  const setScrollRoot = useCallback((node: HTMLDivElement | null) => {
    setScrollEl(node);
  }, []);

  const [rangeMode, setRangeMode] = useState<InterviewRangeMode>('week');
  const [weekField, setWeekField] = useState(() => defaultIsoWeekFieldValue());
  const [monthField, setMonthField] = useState(() => defaultMonthFieldValue());

  const [sortField, setSortField] = useState<InterviewSortField>('scheduledDate');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');

  const [expanded, setExpanded] = useState<string | null>(null);
  const [nextDraft, setNextDraft] = useState({
    meetingLink: '',
    interviewType: 'TECH_1',
    scheduledDate: '',
    scheduledTime: '',
  });

  const sortParam = `${sortField}:${sortDir}`;
  const bounds = useMemo(() => {
    return rangeMode === 'week'
      ? interviewBoundsFromIsoWeekField(weekField)
      : interviewBoundsFromMonthField(monthField);
  }, [rangeMode, weekField, monthField]);

  const rangeKey = rangeMode === 'week' ? weekField : monthField;

  const handleSort = (field: InterviewSortField) => {
    if (field === sortField) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortField(field);
      setSortDir(field === 'meetingLink' || field === 'company' ? 'asc' : 'desc');
    }
  };

  const q = useQuery({
    queryKey: ['interviews', groupId, rangeMode, rangeKey, bounds.from, bounds.to, sortParam] as const,
    enabled: !!groupId,
    queryFn: async ({ queryKey }) => {
      const [, gid, , , from, to, sort] = queryKey;
      const { data } = await api.get(`/groups/${gid}/interviews`, {
        params: { from, to, sort },
      });
      return data as {
        interviews: InterviewRowType[];
        total: number;
        capped?: boolean;
      };
    },
  });

  const patchIv = useMutation({
    mutationFn: async (payload: { id: string; body: Record<string, unknown> }) =>
      api.patch(`/groups/${groupId}/interviews/${payload.id}`, payload.body),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['interviews', groupId] }),
  });

  const createNext = useMutation({
    mutationFn: async (body: Record<string, unknown>) =>
      api.post(`/groups/${groupId}/interviews`, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['interviews', groupId] });
      setExpanded(null);
      setNextDraft({
        meetingLink: '',
        interviewType: 'TECH_1',
        scheduledDate: '',
        scheduledTime: '',
      });
    },
  });

  const deleteIv = useMutation({
    mutationFn: async (id: string) => api.delete(`/groups/${groupId}/interviews/${id}`),
    onSuccess: (_, id) => {
      qc.invalidateQueries({ queryKey: ['interviews', groupId] });
      setExpanded((e) => (e === id ? null : e));
    },
  });

  const allowedNext = useQuery({
    queryKey: ['iv-next-types', groupId, expanded],
    enabled: !!groupId && !!expanded,
    queryFn: async () =>
      (
        await api.get(`/groups/${groupId}/interviews/${expanded}/allowed-next-types`)
      ).data as { types: string[] },
  });

  const rows = q.data?.interviews ?? [];

  if (!groupId) return null;

  return (
    <Stack spacing={2}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} alignItems={{ sm: 'center' }} flexWrap="wrap" useFlexGap>
        <Typography variant="h5">Interviews</Typography>
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
          {formatInterviewRangeCaption(rangeMode, weekField, monthField)} · scheduled or completed in range ·{' '}
          {q.data?.total ?? '—'} shown
        </Typography>
      </Stack>

      {q.isLoading && <LinearProgress />}
      {q.isError && <Alert severity="error">Could not load interviews.</Alert>}
      {q.data?.capped && (
        <Alert severity="warning">More than 2000 rows match; list is truncated.</Alert>
      )}

      <Paper variant="outlined" sx={{ overflow: 'hidden' }}>
        <Box
          ref={setScrollRoot}
          sx={{
            maxHeight: '72vh',
            minHeight: '40vh',
            overflow: 'auto',
          }}
        >
          <InterviewStickyHeader sortField={sortField} sortDir={sortDir} onSort={handleSort} />
          <InterviewVirtualBody
            scrollElement={scrollEl}
            rows={rows}
            expandedId={expanded}
            setExpandedId={setExpanded}
            patchIv={patchIv}
            createNext={createNext}
            deleteInterview={deleteIv}
            nextDraft={nextDraft}
            setNextDraft={setNextDraft}
            allowedNextTypes={allowedNext.data?.types ?? []}
          />
        </Box>
      </Paper>
    </Stack>
  );
}
