import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  Collapse,
  IconButton,
  LinearProgress,
  List,
  ListItem,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import ExpandLessIcon from '@mui/icons-material/ExpandLess';
import api from '../api/client';
import { localYmdFromInstant } from '../utils/dayBounds';
import { toHtmlTimeInputValue } from '../utils/timeInput';
import { FormatStatusBadge } from '../components/FormatStatusBadge';

type PopulatedLink = {
  _id: string;
  url: string;
  sharedJobDescription?: string;
  createdAt: string;
};

export type MyBidListRow = {
  _id: string;
  company: string;
  role: string;
  primaryStacks: string[];
  status: string;
  updatedAt: string;
  groupLinkId: PopulatedLink;
};

function bidSchedulerDayKey(bid: MyBidListRow): string {
  const linkT = new Date(bid.groupLinkId.createdAt).getTime();
  const bidT = new Date(bid.updatedAt).getTime();
  return localYmdFromInstant(Math.max(linkT, bidT));
}

function formatDayHeading(ymd: string): string {
  const [y, m, d] = ymd.split('-').map(Number);
  const dt = new Date(y, m - 1, d);
  return dt.toLocaleDateString(undefined, {
    weekday: 'short',
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}

export default function BidInterviewSchedulePage() {
  const { groupId } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [searchInput, setSearchInput] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');

  useEffect(() => {
    const t = window.setTimeout(() => setDebouncedSearch(searchInput.trim()), 320);
    return () => window.clearTimeout(t);
  }, [searchInput]);

  /** Only `true` = expanded; missing / `false` = collapsed (default: all days collapsed). */
  const [expandedByDay, setExpandedByDay] = useState<Record<string, boolean>>({});

  const [scheduleBidId, setScheduleBidId] = useState<string | null>(null);
  const [ivDraft, setIvDraft] = useState({
    meetingLink: '',
    scheduledDate: '',
    scheduledTime: '',
    recruiter: '',
  });

  const q = useQuery({
    queryKey: ['my-bids', groupId, debouncedSearch] as const,
    enabled: !!groupId,
    queryFn: async ({ queryKey }) => {
      const [, gid, search] = queryKey;
      const { data } = await api.get<{ bids: MyBidListRow[] }>(`/groups/${gid}/my-bids`, {
        params: search ? { search } : {},
      });
      return data.bids;
    },
  });

  const createInterview = useMutation({
    mutationFn: async (bidId: string) =>
      api.post(`/groups/${groupId}/interviews`, {
        meetingLink: ivDraft.meetingLink,
        origin: 'bid',
        interviewType: 'HR',
        bidId,
        scheduledDate: ivDraft.scheduledDate || undefined,
        scheduledTime: ivDraft.scheduledTime || undefined,
        recruiter: ivDraft.recruiter || undefined,
      }),
    onSuccess: () => {
      setScheduleBidId(null);
      setIvDraft({ meetingLink: '', scheduledDate: '', scheduledTime: '', recruiter: '' });
      qc.invalidateQueries({ queryKey: ['interviews', groupId] });
      qc.invalidateQueries({ queryKey: ['bid-board', groupId] });
      qc.invalidateQueries({ queryKey: ['my-bids', groupId] });
      nav(`/g/${groupId}/interviews`);
    },
  });

  const grouped = useMemo(() => {
    const bids = (q.data ?? []).filter((b) => b.status !== 'draft');
    const map = new Map<string, MyBidListRow[]>();
    for (const b of bids) {
      const day = bidSchedulerDayKey(b);
      if (!map.has(day)) map.set(day, []);
      map.get(day)!.push(b);
    }
    const days = [...map.keys()].sort((a, b) => (a < b ? 1 : a > b ? -1 : 0));
    return days.map((day) => ({ day, bids: map.get(day)! }));
  }, [q.data]);

  function isDayExpanded(day: string): boolean {
    return expandedByDay[day] === true;
  }

  function toggleDay(day: string) {
    setExpandedByDay((prev) => ({
      ...prev,
      [day]: !prev[day],
    }));
  }

  if (!groupId) return null;

  return (
    <Stack spacing={2}>
      <Stack spacing={0.5}>
        <Typography variant="h5">Schedule interview from bids</Typography>
        <Typography variant="body2" color="text.secondary">
          Your bids grouped by day (draft bids are hidden). Search matches company, role, or any stack (partial,
          case-insensitive). Days start collapsed — expand a day to see rows.
        </Typography>
      </Stack>

      <TextField
        size="small"
        fullWidth
        placeholder="Search company, role, stacks…"
        value={searchInput}
        onChange={(e) => setSearchInput(e.target.value)}
        inputProps={{ 'aria-label': 'Search bids' }}
      />

      {q.isLoading && <LinearProgress />}
      {q.isError && <Alert severity="error">Could not load your bids.</Alert>}

      {grouped.length === 0 && !q.isLoading && (
        <Typography variant="body2" color="text.secondary">
          No bids match your search.
        </Typography>
      )}

      <Stack spacing={1.5}>
        {grouped.map(({ day, bids }) => (
          <Paper key={day} variant="outlined" sx={{ overflow: 'hidden' }}>
            <Stack
              direction="row"
              alignItems="center"
              spacing={1}
              sx={{
                px: 1.5,
                py: 1,
                bgcolor: 'action.hover',
                borderBottom: 1,
                borderColor: 'divider',
              }}
            >
              <IconButton
                size="small"
                aria-label={isDayExpanded(day) ? `Collapse ${day}` : `Expand ${day}`}
                onClick={() => toggleDay(day)}
              >
                {isDayExpanded(day) ? <ExpandLessIcon /> : <ExpandMoreIcon />}
              </IconButton>
              <Typography variant="subtitle2" fontWeight={700}>
                {formatDayHeading(day)}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                {bids.length} bid{bids.length === 1 ? '' : 's'}
              </Typography>
            </Stack>
            <Collapse in={isDayExpanded(day)} timeout="auto" unmountOnExit={false}>
              <List dense disablePadding>
                {bids.map((b) => {
                  const stacks = (b.primaryStacks || []).join(', ');
                  const open = scheduleBidId === b._id;
                  return (
                    <ListItem
                      key={b._id}
                      disablePadding
                      sx={{ flexDirection: 'column', alignItems: 'stretch' }}
                    >
                      <Box
                        sx={{
                          display: 'grid',
                          gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr minmax(120px,0.8fr) auto' },
                          gap: 1,
                          alignItems: 'center',
                          px: 1.5,
                          py: 1,
                          borderBottom: 1,
                          borderColor: 'divider',
                        }}
                      >
                        <Box sx={{ minWidth: 0 }}>
                          <Typography variant="body2" fontWeight={600} noWrap title={b.company}>
                            {b.company?.trim() || '—'}
                          </Typography>
                          <Typography variant="caption" color="text.secondary" noWrap title={b.role}>
                            {b.role?.trim() || '—'}
                          </Typography>
                        </Box>
                        <Typography
                          variant="caption"
                          color="text.secondary"
                          sx={{ minWidth: 0 }}
                          noWrap
                          title={stacks}
                        >
                          {stacks || '—'}
                        </Typography>
                        <Box sx={{ justifySelf: { xs: 'start', sm: 'center' } }}>
                          <FormatStatusBadge kind="bid" status={b.status} />
                        </Box>
                        <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap>
                          <Button
                            size="small"
                            href={b.groupLinkId.url}
                            target="_blank"
                            rel="noreferrer"
                            sx={{ minWidth: 0 }}
                          >
                            Job link
                          </Button>
                          <Button
                            size="small"
                            variant={open ? 'contained' : 'outlined'}
                            onClick={() =>
                              setScheduleBidId((id) => (id === b._id ? null : b._id))
                            }
                          >
                            {open ? 'Cancel' : 'Schedule'}
                          </Button>
                        </Stack>
                      </Box>
                      <Collapse in={open} timeout="auto">
                        <Box
                          sx={{
                            px: 1.5,
                            py: 1.5,
                            bgcolor: 'background.default',
                            borderBottom: 1,
                            borderColor: 'divider',
                          }}
                        >
                          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} flexWrap="wrap" useFlexGap>
                            <TextField
                              value={ivDraft.meetingLink}
                              onChange={(e) =>
                                setIvDraft((d) => ({ ...d, meetingLink: e.target.value }))
                              }
                              fullWidth
                              size="small"
                              required
                              placeholder="Meeting link"
                              inputProps={{ 'aria-label': 'Meeting link' }}
                            />
                            <TextField
                              type="date"
                              value={ivDraft.scheduledDate}
                              onChange={(e) =>
                                setIvDraft((d) => ({ ...d, scheduledDate: e.target.value }))
                              }
                              size="small"
                              inputProps={{ 'aria-label': 'Interview date' }}
                              sx={{ minWidth: 140 }}
                            />
                            <TextField
                              type="time"
                              InputLabelProps={{ shrink: true }}
                              label="Time"
                              value={toHtmlTimeInputValue(ivDraft.scheduledTime)}
                              onChange={(e) =>
                                setIvDraft((d) => ({ ...d, scheduledTime: e.target.value }))
                              }
                              size="small"
                              slotProps={{
                                htmlInput: { step: 60, 'aria-label': 'Interview time' },
                              }}
                              sx={{ minWidth: 108 }}
                            />
                            <TextField
                              value={ivDraft.recruiter}
                              onChange={(e) =>
                                setIvDraft((d) => ({ ...d, recruiter: e.target.value }))
                              }
                              size="small"
                              placeholder="Recruiter"
                              inputProps={{ 'aria-label': 'Recruiter' }}
                              sx={{ minWidth: 120 }}
                            />
                            <Button
                              variant="contained"
                              onClick={() => createInterview.mutate(b._id)}
                              disabled={
                                !ivDraft.meetingLink.trim() || createInterview.isPending
                              }
                            >
                              Create interview
                            </Button>
                          </Stack>
                        </Box>
                      </Collapse>
                    </ListItem>
                  );
                })}
              </List>
            </Collapse>
          </Paper>
        ))}
      </Stack>
    </Stack>
  );
}
