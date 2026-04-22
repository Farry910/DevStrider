import { useCallback, useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  Box,
  FormControlLabel,
  LinearProgress,
  Paper,
  Stack,
  Switch,
  TextField,
  Typography,
  Button,
  Tooltip,
} from '@mui/material';
import api from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { isAxiosError } from 'axios';
import { parseFastFeedLine } from '../utils/parseFastFeed';
import { localDayIsoRange, todayLocalYmd } from '../utils/dayBounds';
import { BidBoardStickyHeader } from '../components/bid/BidBoardStickyHeader';
import { BidBoardVirtualBody, type BoardRow } from '../components/bid/BidBoardVirtualBody';
import { bidBoardRowGridSx, type BidSortField } from '../components/bid/bidBoardGrid';
import { useBidBoardSocketInvalidation } from '../hooks/useBidBoardSocket';

export default function BidPanelPage() {
  const { user } = useAuth();
  const { groupId } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  useBidBoardSocketInvalidation(groupId, Boolean(groupId && user), qc);
  /** State (not ref-only) so the virtualizer rebinds when the scroll root mounts; ref alone does not re-render. */
  const [scrollEl, setScrollEl] = useState<HTMLDivElement | null>(null);
  const setScrollRoot = useCallback((node: HTMLDivElement | null) => {
    setScrollEl(node);
  }, []);

  const [selectedDay, setSelectedDay] = useState(todayLocalYmd);
  const [sortField, setSortField] = useState<BidSortField>('linkCreatedAt');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');

  const [composerUrl, setComposerUrl] = useState('');
  const composerUrlInputRef = useRef<HTMLInputElement | null>(null);
  /** Bumps after a successful composer submit so a effect can refocus the input after paint. */
  const [composerFocusTick, setComposerFocusTick] = useState(0);

  const [fastFeed, setFastFeed] = useState<Record<string, string>>({});
  const [expandedBid, setExpandedBid] = useState<string | null>(null);
  const [ivDraft, setIvDraft] = useState({
    meetingLink: '',
    scheduledDate: '',
    scheduledTime: '',
    recruiter: '',
  });

  /** Past days: when on, include link-only / empty bid rows (same data members see on today). */
  const [showPastLinkOnlyRows, setShowPastLinkOnlyRows] = useState(false);

  const sortParam = `${sortField}:${sortDir}`;
  const biddingEnabled = selectedDay === todayLocalYmd();

  useEffect(() => {
    if (!biddingEnabled) {
      setComposerUrl('');
    }
  }, [biddingEnabled, selectedDay]);

  useEffect(() => {
    if (composerFocusTick === 0) return;
    const focus = () => {
      const el = composerUrlInputRef.current;
      if (el && !el.disabled) el.focus({ preventScroll: true });
    };
    focus();
    const t0 = window.setTimeout(focus, 0);
    const t1 = window.setTimeout(focus, 100);
    return () => {
      clearTimeout(t0);
      clearTimeout(t1);
    };
  }, [composerFocusTick]);

  const handleSort = (field: BidSortField) => {
    if (field === sortField) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortField(field);
      setSortDir('desc');
    }
  };

  const q = useQuery({
    queryKey: ['bid-board', groupId, selectedDay, sortParam, showPastLinkOnlyRows] as const,
    enabled: !!groupId,
    queryFn: async ({ queryKey }) => {
      const [, gid, day, sort, showEmptyPast] = queryKey;
      const { from, to } = localDayIsoRange(day);
      const isToday = day === todayLocalYmd();
      const excludeLinkOnlyPast = !isToday && !showEmptyPast;
      const { data } = await api.get(`/groups/${gid}/bid-board`, {
        params: {
          from,
          to,
          sort,
          ...(excludeLinkOnlyPast ? { excludeLinkOnly: 'true' } : {}),
        },
      });
      return data as {
        rows: BoardRow[];
        total: number;
        capped?: boolean;
      };
    },
  });

  const addLink = useMutation({
    mutationFn: async (url: string) => {
      const { from, to } = localDayIsoRange(todayLocalYmd());
      const { data } = await api.post(`/groups/${groupId}/links`, { url, from, to });
      return { data, url };
    },
    onSuccess: () => {
      setComposerUrl('');
      void qc.invalidateQueries({ queryKey: ['bid-board', groupId] });
      setComposerFocusTick((t) => t + 1);
    },
  });

  const groupMeQ = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () => (await api.get(`/groups/${groupId}/me`)).data as { role: string },
  });
  const isGroupOwner = groupMeQ.data?.role === 'creator';

  const patchLinkUseless = useMutation({
    mutationFn: async (vars: { linkId: string; useless: boolean }) => {
      await api.patch(`/groups/${groupId}/links/${vars.linkId}/useless`, { useless: vars.useless });
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bid-board', groupId] }),
  });

  const refreshJunkLinks = useMutation({
    mutationFn: async () => {
      const { data } = await api.post<{ removed: number }>(`/groups/${groupId}/links/refresh-junk`);
      return data;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bid-board', groupId] }),
  });

  const patchBid = useMutation({
    mutationFn: async (payload: { bidId: string; body: Record<string, unknown> }) => {
      const { from, to } = localDayIsoRange(selectedDay);
      return api.patch(`/groups/${groupId}/bids/${payload.bidId}`, payload.body, {
        params: { from, to },
      });
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bid-board', groupId] }),
  });

  const deleteBid = useMutation({
    mutationFn: async (bidId: string) => {
      const { from, to } = localDayIsoRange(selectedDay);
      return api.delete(`/groups/${groupId}/bids/${bidId}`, {
        params: { from, to },
      });
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bid-board', groupId] }),
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
      setExpandedBid(null);
      setIvDraft({ meetingLink: '', scheduledDate: '', scheduledTime: '', recruiter: '' });
      qc.invalidateQueries({ queryKey: ['interviews', groupId] });
      qc.invalidateQueries({ queryKey: ['bid-board', groupId] });
      nav(`/g/${groupId}/interviews`);
    },
  });

  const rows = q.data?.rows ?? [];
  const eligibleJunkCount = rows.filter((r) => r.link.markedUselessAt && r.link.junkPurgeEligible)
    .length;

  async function commitFastFeed(linkId: string, existingBidId: string | null) {
    if (!biddingEnabled || !groupId) return;
    const { from, to } = localDayIsoRange(todayLocalYmd());
    const raw = fastFeed[linkId] ?? '';
    const parsed = parseFastFeedLine(raw);
    if (!parsed) return;
    try {
      let bidId = existingBidId;
      if (!bidId) {
        const { data } = await api.post<{ bid: { _id: string } }>(
          `/groups/${groupId}/links/${linkId}/my-bid`,
          {},
          { params: { from, to } }
        );
        bidId = String(data.bid._id);
      }
      await api.patch(
        `/groups/${groupId}/bids/${bidId}`,
        {
          resumeId: parsed.resumeId,
          company: parsed.company,
          role: parsed.role,
          primaryStacks: parsed.primaryStacks,
          fromFastFeed: true,
        },
        { params: { from, to } }
      );
      setFastFeed((prev) => {
        const next = { ...prev };
        delete next[linkId];
        return next;
      });
      await qc.invalidateQueries({ queryKey: ['bid-board', groupId] });
    } catch (e) {
      console.error(e);
    }
  }

  function submitComposerLink() {
    const u = composerUrl.trim();
    if (!biddingEnabled || u.length < 5 || addLink.isPending) return;
    addLink.mutate(u);
  }

  if (!groupId) return null;

  return (
    <Stack spacing={2}>
      <Stack direction="row" alignItems="center" spacing={1.5} flexWrap="wrap" useFlexGap gap={1}>
        <Typography variant="h5">Bid board</Typography>
        {isGroupOwner && (
          <Tooltip
            title={
              eligibleJunkCount > 0
                ? 'Remove junk now, or wait — eligible links are also removed automatically at least 10 minutes after they were marked useless.'
                : 'No junk links ready to remove. Eligible links auto-remove ≥10 min after the useless mark, or use this button to remove immediately.'
            }
          >
            <span>
              <Button
                size="small"
                variant="outlined"
                disabled={eligibleJunkCount === 0 || refreshJunkLinks.isPending}
                onClick={() => {
                  if (
                    !window.confirm(
                      `Remove ${eligibleJunkCount} junk link(s)? This cannot be undone.`
                    )
                  ) {
                    return;
                  }
                  refreshJunkLinks.mutate();
                }}
              >
                Refresh junk links
              </Button>
            </span>
          </Tooltip>
        )}
        <TextField
          type="date"
          size="small"
          value={selectedDay}
          onChange={(e) => setSelectedDay(e.target.value)}
          inputProps={{ 'aria-label': 'Day' }}
          sx={{
            width: 'auto',
            maxWidth: 118,
            '& .MuiInputBase-input': { py: 0.35, px: 0.75, fontSize: '0.8125rem' },
          }}
        />
        {q.data != null && (
          <Typography variant="caption" color="text.secondary">
            {q.data.total}
          </Typography>
        )}
        {!biddingEnabled && (
          <FormControlLabel
            sx={{ ml: 0, mr: 0 }}
            control={
              <Switch
                size="small"
                checked={showPastLinkOnlyRows}
                onChange={(_, c) => setShowPastLinkOnlyRows(c)}
                inputProps={{ 'aria-label': 'Show link-only and empty bid rows for this day' }}
              />
            }
            label={
              <Typography variant="body2" color="text.secondary" component="span">
                Show empty / link-only rows
              </Typography>
            }
          />
        )}
      </Stack>
      {q.isLoading && <LinearProgress />}
      {q.isError && <Alert severity="error">Could not load bid board.</Alert>}
      {q.data?.capped && (
        <Alert severity="warning">This day has over 5000 rows; only the first 5000 are shown.</Alert>
      )}
      {!biddingEnabled && (
        <Alert severity="info">
          Past day — you can still edit, remove, or schedule interviews from bids shown for this date.
          Use today&apos;s date to add new job URLs.
        </Alert>
      )}
      {addLink.isError && (
        <Alert severity="error">
          {isAxiosError(addLink.error) &&
          addLink.error.response?.data &&
          typeof (addLink.error.response.data as { error?: unknown }).error === 'string'
            ? (addLink.error.response.data as { error: string }).error
            : 'Could not add link. Check URL and try again.'}
        </Alert>
      )}
      {deleteBid.isError && (
        <Alert severity="error">
          {isAxiosError(deleteBid.error) &&
          deleteBid.error.response?.data &&
          typeof (deleteBid.error.response.data as { error?: unknown }).error === 'string'
            ? (deleteBid.error.response.data as { error: string }).error
            : 'Could not remove bid.'}
        </Alert>
      )}
      {refreshJunkLinks.isError && (
        <Alert severity="error">
          {isAxiosError(refreshJunkLinks.error) &&
          refreshJunkLinks.error.response?.data &&
          typeof (refreshJunkLinks.error.response.data as { error?: unknown }).error === 'string'
            ? (refreshJunkLinks.error.response.data as { error: string }).error
            : 'Could not refresh junk links.'}
        </Alert>
      )}
      <Paper variant="outlined" sx={{ overflow: 'hidden' }}>
        <Box
          ref={setScrollRoot}
          sx={{
            maxHeight: '72vh',
            /* Virtualizer skips rendering when outerSize === 0; minHeight guarantees a measurable viewport. */
            minHeight: '40vh',
            overflow: 'auto',
          }}
        >
          <BidBoardStickyHeader sortField={sortField} sortDir={sortDir} onSort={handleSort} />
          {biddingEnabled && (
            <Box
              sx={{
                ...bidBoardRowGridSx,
                borderBottom: 1,
                borderColor: 'divider',
                bgcolor: 'action.hover',
              }}
            >
              <TextField
                sx={{ gridColumn: '1 / -1' }}
                value={composerUrl}
                onChange={(e) => setComposerUrl(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    submitComposerLink();
                  }
                }}
                placeholder="Add job URL — row appears in the list below"
                disabled={addLink.isPending}
                fullWidth
                variant="outlined"
                size="small"
                inputProps={{ 'aria-label': 'New job URL' }}
                inputRef={composerUrlInputRef}
              />
            </Box>
          )}
          <BidBoardVirtualBody
            scrollElement={scrollEl}
            rows={rows}
            expandedBidId={expandedBid}
            setExpandedBid={setExpandedBid}
            fastFeed={fastFeed}
            setFastFeed={setFastFeed}
            commitFastFeed={commitFastFeed}
            patchBid={patchBid}
            readOnly={false}
            deleteBid={deleteBid}
            ivDraft={ivDraft}
            setIvDraft={setIvDraft}
            createInterview={createInterview}
            currentUserId={user?.id}
            allowNewInputFlow={biddingEnabled}
            patchLinkUseless={patchLinkUseless}
          />
        </Box>
      </Paper>
    </Stack>
  );
}
