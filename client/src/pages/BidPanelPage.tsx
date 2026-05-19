import { useCallback, useEffect, useRef, useState } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  Autocomplete,
  Box,
  Checkbox,
  Chip,
  FormControlLabel,
  IconButton,
  InputAdornment,
  LinearProgress,
  Paper,
  Stack,
  Switch,
  TextField,
  Typography,
  Button,
  Tooltip,
} from '@mui/material';
import ClearIcon from '@mui/icons-material/Clear';
import api from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { isAxiosError } from 'axios';
import { parseFastFeedLine } from '../utils/parseFastFeed';
import { localDayIsoRange, todayLocalYmd } from '../utils/dayBounds';
import { BidBoardStickyHeader } from '../components/bid/BidBoardStickyHeader';
import { BidBoardVirtualBody, type BoardRow } from '../components/bid/BidBoardVirtualBody';
import { bidBoardRowGridSx, type BidSortField } from '../components/bid/bidBoardGrid';
import { BatchAddDialog } from '../components/bid/BatchAddDialog';
import { useBidBoardSocketInvalidation } from '../hooks/useBidBoardSocket';
import { getMyProfile } from '../api/profile';
import { useGroupPermissions } from '../hooks/useGroupPermissions';
import { DownloadCsvButton } from '../components/DownloadCsvButton';
import {
  isOptimisticId,
  makeOptimisticLinkRow,
  patchAllBoardQueries,
  rollbackBoardQueries,
  type BidBoardData,
} from '../utils/optimisticBidBoard';

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

  const [batchOpen, setBatchOpen] = useState(false);

  /** Link column filter: only show links created by these user IDs. Empty = no filter. */
  const [filterUserIds, setFilterUserIds] = useState<string[]>([]);
  /** Role column filter: substring match against my own bid.role OR peer's applied role. */
  const [filterRole, setFilterRole] = useState('');

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

  const filterRoleTrimmed = filterRole.trim();
  const filterUserIdsKey = filterUserIds.join(',');
  const q = useQuery({
    queryKey: [
      'bid-board',
      groupId,
      selectedDay,
      sortParam,
      showPastLinkOnlyRows,
      filterUserIdsKey,
      filterRoleTrimmed,
    ] as const,
    enabled: !!groupId,
    staleTime: 30_000,
    placeholderData: keepPreviousData,
    queryFn: async ({ queryKey }) => {
      const [, gid, day, sort, showEmptyPast, userIdsCsv, roleText] = queryKey;
      const { from, to } = localDayIsoRange(day);
      const isToday = day === todayLocalYmd();
      const excludeLinkOnlyPast = !isToday && !showEmptyPast;
      const { data } = await api.get(`/groups/${gid}/bid-board`, {
        params: {
          from,
          to,
          sort,
          ...(excludeLinkOnlyPast ? { excludeLinkOnly: 'true' } : {}),
          ...(userIdsCsv ? { f_createdByUserIds: userIdsCsv } : {}),
          ...(roleText ? { f_role: roleText } : {}),
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
    /**
     * Optimistic add: clear input, insert a placeholder row immediately. Server may still take
     * ~1s on free Render → free Atlas (cross-region). Reconcile via invalidate on settle.
     */
    mutationFn: async (url: string) => {
      const { from, to } = localDayIsoRange(todayLocalYmd());
      const { data } = await api.post(`/groups/${groupId}/links`, { url, from, to });
      return { data, url };
    },
    onMutate: async (url: string) => {
      if (!groupId || !user) return { snapshots: [] };
      await qc.cancelQueries({ queryKey: ['bid-board', groupId] });
      const optimisticRow = makeOptimisticLinkRow({
        url,
        userId: user.id,
        nickname: user.nickname || '',
        avatarId: user.avatarId || 'initial',
      });
      const snapshots = patchAllBoardQueries(qc, groupId, (prev: BidBoardData) => ({
        ...prev,
        rows: [optimisticRow, ...prev.rows],
        total: prev.total + 1,
      }));
      setComposerUrl('');
      setComposerFocusTick((t) => t + 1);
      return { snapshots };
    },
    onError: (_err, _url, ctx) => {
      if (ctx?.snapshots) rollbackBoardQueries(qc, ctx.snapshots);
    },
    onSettled: () => {
      if (groupId) {
        void qc.invalidateQueries({ queryKey: ['bid-board', groupId] });
      }
    },
  });

  const groupMeQ = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () => (await api.get(`/groups/${groupId}/me`)).data as { role: string },
  });
  const isGroupOwner = groupMeQ.data?.role === 'creator';

  const profileQ = useQuery({
    queryKey: ['profile', 'me'] as const,
    queryFn: getMyProfile,
    staleTime: 5 * 60 * 1000,
  });

  const perms = useGroupPermissions(groupId);

  /** Group members for the "Link by user" filter dropdown. Same endpoint used by GroupMembersPanel. */
  const filterMembersQ = useQuery({
    queryKey: ['group', groupId, 'members-detailed'] as const,
    enabled: !!groupId,
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/members-detailed`)).data as {
        members: Array<{ userId: string; nickname: string; email: string }>;
      },
    staleTime: 5 * 60 * 1000,
  });

  const patchLinkUseless = useMutation({
    mutationFn: async (vars: { linkId: string; useless: boolean }) => {
      if (isOptimisticId(vars.linkId)) return;
      await api.patch(`/groups/${groupId}/links/${vars.linkId}/useless`, { useless: vars.useless });
    },
    onMutate: async (vars) => {
      if (!groupId || isOptimisticId(vars.linkId)) return { snapshots: [] };
      await qc.cancelQueries({ queryKey: ['bid-board', groupId] });
      const stamp = vars.useless ? new Date().toISOString() : null;
      const snapshots = patchAllBoardQueries(qc, groupId, (prev) => ({
        ...prev,
        rows: prev.rows.map((r) =>
          r.link.id === vars.linkId
            ? { ...r, link: { ...r.link, markedUselessAt: stamp } }
            : r
        ),
      }));
      return { snapshots };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.snapshots) rollbackBoardQueries(qc, ctx.snapshots);
    },
    onSettled: () => qc.invalidateQueries({ queryKey: ['bid-board', groupId] }),
  });

  const refreshJunkLinks = useMutation({
    mutationFn: async () => {
      const { data } = await api.post<{ removed: number }>(`/groups/${groupId}/links/refresh-junk`);
      return data;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['bid-board', groupId] }),
  });

  const carryoverCountQ = useQuery({
    queryKey: ['bid-board', groupId, 'carryover-count'] as const,
    enabled: !!groupId,
    queryFn: async () => {
      const { data } = await api.get<{ count: number }>(
        `/groups/${groupId}/links/carryover-count`
      );
      return data;
    },
  });

  const carryOverYesterday = useMutation({
    mutationFn: async () => {
      const { data } = await api.post<{ carriedOver: number }>(
        `/groups/${groupId}/links/carryover`
      );
      return data;
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['bid-board', groupId] });
    },
  });

  const patchBid = useMutation({
    mutationFn: async (payload: { bidId: string; body: Record<string, unknown> }) => {
      if (isOptimisticId(payload.bidId)) return;
      const { from, to } = localDayIsoRange(selectedDay);
      return api.patch(`/groups/${groupId}/bids/${payload.bidId}`, payload.body, {
        params: { from, to },
      });
    },
    onMutate: async (payload) => {
      if (!groupId || isOptimisticId(payload.bidId)) return { snapshots: [] };
      await qc.cancelQueries({ queryKey: ['bid-board', groupId] });
      const body = payload.body;
      const snapshots = patchAllBoardQueries(qc, groupId, (prev) => ({
        ...prev,
        rows: prev.rows.map((r) => {
          if (!r.myBid || r.myBid.id !== payload.bidId) return r;
          return {
            ...r,
            myBid: {
              ...r.myBid,
              ...('resumeId' in body ? { resumeId: String(body.resumeId ?? '') } : {}),
              ...('company' in body ? { company: String(body.company ?? '') } : {}),
              ...('role' in body ? { role: String(body.role ?? '') } : {}),
              ...('primaryStacks' in body
                ? { primaryStacks: Array.isArray(body.primaryStacks) ? (body.primaryStacks as string[]) : [] }
                : {}),
              ...('status' in body ? { status: String(body.status ?? r.myBid.status) } : {}),
              ...('origin' in body ? { origin: String(body.origin ?? '') } : {}),
              ...('jobDescription' in body
                ? { jobDescription: String(body.jobDescription ?? '') }
                : {}),
              ...('gptResumeContent' in body
                ? { gptResumeContent: String(body.gptResumeContent ?? '') }
                : {}),
              ...('comment' in body ? { comment: String(body.comment ?? '') } : {}),
              updatedAt: new Date().toISOString(),
            },
          };
        }),
      }));
      return { snapshots };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.snapshots) rollbackBoardQueries(qc, ctx.snapshots);
    },
    onSettled: () => qc.invalidateQueries({ queryKey: ['bid-board', groupId] }),
  });

  const deleteBid = useMutation({
    mutationFn: async (bidId: string) => {
      if (isOptimisticId(bidId)) return;
      const { from, to } = localDayIsoRange(selectedDay);
      return api.delete(`/groups/${groupId}/bids/${bidId}`, {
        params: { from, to },
      });
    },
    onMutate: async (bidId) => {
      if (!groupId || isOptimisticId(bidId)) return { snapshots: [] };
      await qc.cancelQueries({ queryKey: ['bid-board', groupId] });
      const snapshots = patchAllBoardQueries(qc, groupId, (prev) => ({
        ...prev,
        rows: prev.rows.filter((r) => r.myBid?.id !== bidId),
        total: Math.max(0, prev.total - 1),
      }));
      return { snapshots };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.snapshots) rollbackBoardQueries(qc, ctx.snapshots);
    },
    onSettled: () => qc.invalidateQueries({ queryKey: ['bid-board', groupId] }),
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
  const appliedCount = rows.filter((r) => r.myBid?.status === 'applied').length;

  async function commitFastFeed(linkId: string, existingBidId: string | null) {
    if (!biddingEnabled || !groupId) return;
    if (isOptimisticId(linkId)) return; // parent link not yet saved server-side
    const { from, to } = localDayIsoRange(todayLocalYmd());
    const raw = fastFeed[linkId] ?? '';
    const parsed = parseFastFeedLine(raw);
    if (!parsed) return;

    /** Optimistic: clear the fast-feed input and mark the row applied immediately. */
    setFastFeed((prev) => {
      const next = { ...prev };
      delete next[linkId];
      return next;
    });
    await qc.cancelQueries({ queryKey: ['bid-board', groupId] });
    const snapshots = patchAllBoardQueries(qc, groupId, (prev) => ({
      ...prev,
      rows: prev.rows.map((r) => {
        if (r.link.id !== linkId) return r;
        const base = r.myBid ?? {
          id: `optimistic-${linkId}-bid`,
          resumeId: '',
          company: '',
          role: '',
          primaryStacks: [],
          status: 'draft',
          origin: 'LinkedIn',
          jobDescription: '',
          gptResumeContent: '',
          comment: '',
          firstCreatedAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          lastModifiedBy: null,
        };
        return {
          ...r,
          myBid: {
            ...base,
            resumeId: parsed.resumeId,
            company: parsed.company,
            role: parsed.role,
            primaryStacks: parsed.primaryStacks,
            status: 'applied',
            updatedAt: new Date().toISOString(),
          },
        };
      }),
    }));

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
      await qc.invalidateQueries({ queryKey: ['bid-board', groupId] });
    } catch (e) {
      console.error(e);
      rollbackBoardQueries(qc, snapshots);
      setFastFeed((prev) => ({ ...prev, [linkId]: raw }));
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
        {perms.canExport && groupId && (
          <DownloadCsvButton groupId={groupId} kind="bids" />
        )}
        {biddingEnabled && perms.canBid && (carryoverCountQ.data?.count ?? 0) > 0 && (
          <Tooltip title="Move yesterday's links you didn't apply to onto today's board (other members are unaffected).">
            <span>
              <Button
                size="small"
                variant="outlined"
                disabled={carryOverYesterday.isPending}
                onClick={() => {
                  const n = carryoverCountQ.data?.count ?? 0;
                  if (n === 0) return;
                  if (
                    !window.confirm(
                      `Carry over ${n} unfinished link${n === 1 ? '' : 's'} from yesterday onto today's board?`
                    )
                  ) {
                    return;
                  }
                  carryOverYesterday.mutate();
                }}
              >
                Carry over yesterday ({carryoverCountQ.data?.count ?? 0})
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
        {q.data != null && (
          <Tooltip
            title={`${appliedCount} bid${appliedCount === 1 ? '' : 's'} with status "applied" on ${selectedDay}`}
          >
            <Chip
              size="small"
              color={appliedCount > 0 ? 'success' : 'default'}
              variant={appliedCount > 0 ? 'filled' : 'outlined'}
              label={`${appliedCount} applied`}
              sx={{ height: 22, '& .MuiChip-label': { px: 0.85, fontSize: '0.7rem' } }}
            />
          </Tooltip>
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
      {(q.isLoading || (q.isPlaceholderData && q.isFetching)) && <LinearProgress />}
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
      {carryOverYesterday.isError && (
        <Alert severity="error">
          {isAxiosError(carryOverYesterday.error) &&
          carryOverYesterday.error.response?.data &&
          typeof (carryOverYesterday.error.response.data as { error?: unknown }).error === 'string'
            ? (carryOverYesterday.error.response.data as { error: string }).error
            : 'Could not carry over yesterday’s links.'}
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
          <BidBoardStickyHeader
            sortField={sortField}
            sortDir={sortDir}
            onSort={handleSort}
            filterByColumn={{
              link: (
                <Autocomplete
                  multiple
                  size="small"
                  options={filterMembersQ.data?.members ?? []}
                  getOptionLabel={(o) => o.nickname || o.email}
                  isOptionEqualToValue={(a, b) => a.userId === b.userId}
                  value={(filterMembersQ.data?.members ?? []).filter((m) =>
                    filterUserIds.includes(m.userId)
                  )}
                  onChange={(_, v) => setFilterUserIds(v.map((x) => x.userId))}
                  disableCloseOnSelect
                  limitTags={1}
                  renderOption={(props, option, { selected }) => (
                    <li {...props} key={option.userId}>
                      <Checkbox size="small" checked={selected} sx={{ p: 0.5, mr: 0.5 }} />
                      <Typography variant="body2" noWrap>
                        {option.nickname || option.email}
                      </Typography>
                    </li>
                  )}
                  renderInput={(params) => (
                    <TextField
                      {...params}
                      placeholder={filterUserIds.length === 0 ? 'All users' : ''}
                      inputProps={{
                        ...params.inputProps,
                        'aria-label': 'Filter links by user',
                      }}
                      sx={{
                        '& .MuiInputBase-root': {
                          fontSize: '0.75rem',
                          minHeight: 28,
                          py: '2px',
                        },
                      }}
                    />
                  )}
                  sx={{ width: '100%', minWidth: 0 }}
                />
              ),
              role: (
                <TextField
                  size="small"
                  value={filterRole}
                  onChange={(e) => setFilterRole(e.target.value)}
                  placeholder="Filter…"
                  inputProps={{ 'aria-label': 'Filter role substring' }}
                  InputProps={{
                    endAdornment: filterRole ? (
                      <InputAdornment position="end">
                        <IconButton
                          size="small"
                          aria-label="Clear role filter"
                          onClick={() => setFilterRole('')}
                          sx={{ p: 0.25 }}
                        >
                          <ClearIcon sx={{ fontSize: 14 }} />
                        </IconButton>
                      </InputAdornment>
                    ) : null,
                  }}
                  sx={{
                    width: '100%',
                    '& .MuiInputBase-root': {
                      fontSize: '0.75rem',
                      minHeight: 28,
                      py: '2px',
                    },
                  }}
                />
              ),
            }}
          />
          {biddingEnabled && perms.canBid && (
            <Box
              sx={{
                ...bidBoardRowGridSx,
                borderBottom: 1,
                borderColor: 'divider',
                bgcolor: 'action.hover',
              }}
            >
              <Box
                sx={{
                  gridColumn: '1 / -1',
                  display: 'flex',
                  alignItems: 'center',
                  gap: 0.5,
                  minWidth: 0,
                }}
              >
                <TextField
                  sx={{ flex: 1, minWidth: 0 }}
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
                <Tooltip title="Paste a list of links (with optional fast feed) — preview parsed rows before adding.">
                  <Button
                    size="small"
                    variant="outlined"
                    onClick={() => setBatchOpen(true)}
                    sx={{ flexShrink: 0, whiteSpace: 'nowrap' }}
                  >
                    Batch add
                  </Button>
                </Tooltip>
              </Box>
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
            deleteBid={deleteBid}
            ivDraft={ivDraft}
            setIvDraft={setIvDraft}
            createInterview={createInterview}
            currentUserId={user?.id}
            allowNewInputFlow={biddingEnabled && perms.canBid}
            patchLinkUseless={patchLinkUseless}
            myProfile={profileQ.data ?? null}
            readOnly={!perms.canBid}
          />
        </Box>
      </Paper>
      <BatchAddDialog open={batchOpen} onClose={() => setBatchOpen(false)} />
    </Stack>
  );
}
