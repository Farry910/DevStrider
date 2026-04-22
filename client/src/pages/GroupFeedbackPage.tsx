import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useLocation, useParams } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  ButtonGroup,
  Chip,
  Divider,
  FormControl,
  InputLabel,
  LinearProgress,
  MenuItem,
  Paper,
  Select,
  Stack,
  Tab,
  Tabs,
  TextField,
  Typography,
} from '@mui/material';
import api from '../api/client';
import { useAuth } from '../auth/AuthContext';

type GroupMe = {
  group: { _id: string; name: string };
  isMember: boolean;
  role: 'creator' | 'member' | 'none';
};

type FeedbackCategory = 'general' | 'bug' | 'idea' | 'other';
type FeedbackStatus = 'open' | 'resolved' | 'ignored';

type FeedbackRow = {
  _id: string;
  category: FeedbackCategory;
  message: string;
  pagePath?: string;
  status?: FeedbackStatus;
  ownerComment?: string;
  ownerCommentAt?: string | null;
  createdAt: string;
  userId: { _id: string; nickname: string; email: string } | null;
};

type FeedbackListResponse = {
  feedback: FeedbackRow[];
  isOwner?: boolean;
};

type FeedbackPatchVars = { id: string; status?: FeedbackStatus; ownerComment?: string };

type FeedbackPatchMut = {
  mutate: (args: FeedbackPatchVars) => void;
  isPending: boolean;
  variables?: FeedbackPatchVars;
};

const categoryLabels: Record<FeedbackCategory, string> = {
  general: 'General',
  bug: 'Bug / problem',
  idea: 'Feature idea',
  other: 'Other',
};

const statusLabels: Record<FeedbackStatus, string> = {
  open: 'Open',
  resolved: 'Resolved',
  ignored: 'Ignored',
};

function tabProps(index: number) {
  return { id: `feedback-tab-${index}`, 'aria-controls': `feedback-tabpanel-${index}` };
}

function OwnerFeedbackRow({
  row,
  currentUserId,
  patchMut,
}: {
  row: FeedbackRow;
  currentUserId: string;
  patchMut: FeedbackPatchMut;
}) {
  const [draft, setDraft] = useState(row.ownerComment ?? '');
  useEffect(() => {
    setDraft(row.ownerComment ?? '');
  }, [row._id, row.ownerComment]);

  const busy = patchMut.isPending && patchMut.variables?.id === row._id;

  return (
    <FeedbackCard
      row={row}
      showAuthor
      showStatus={row.category !== 'general'}
      isOwner
      currentUserId={currentUserId}
      commentDraft={draft}
      onCommentDraft={setDraft}
      onSaveComment={() => patchMut.mutate({ id: row._id, ownerComment: draft })}
      onSetStatus={(s) => patchMut.mutate({ id: row._id, status: s })}
      busy={busy}
    />
  );
}

function FeedbackCard({
  row,
  showAuthor,
  showStatus,
  isOwner,
  currentUserId,
  commentDraft,
  onCommentDraft,
  onSaveComment,
  onSetStatus,
  busy,
}: {
  row: FeedbackRow;
  showAuthor: boolean;
  showStatus: boolean;
  isOwner: boolean;
  currentUserId: string;
  commentDraft: string;
  onCommentDraft: (v: string) => void;
  onSaveComment: () => void;
  onSetStatus: (s: FeedbackStatus) => void;
  busy: boolean;
}) {
  const author =
    row.userId &&
    (row.userId.nickname || row.userId.email || String(row.userId._id).slice(-6));
  const isMine = row.userId && String(row.userId._id) === currentUserId;
  const st = row.status ?? 'open';

  return (
    <Paper variant="outlined" sx={{ p: 2, borderRadius: 2 }}>
      <Stack spacing={1.25}>
        <Stack direction="row" alignItems="center" gap={1} flexWrap="wrap">
          <Chip size="small" label={categoryLabels[row.category]} variant="outlined" />
          {showStatus && row.category !== 'general' && (
            <Chip
              size="small"
              label={statusLabels[st]}
              color={st === 'resolved' ? 'success' : st === 'ignored' ? 'default' : 'primary'}
              variant={st === 'open' ? 'filled' : 'outlined'}
            />
          )}
          <Typography variant="caption" color="text.secondary">
            {new Date(row.createdAt).toLocaleString()}
          </Typography>
          {showAuthor && author && (
            <Typography variant="caption" color="text.secondary">
              · {author}
              {isMine ? ' (you)' : ''}
            </Typography>
          )}
        </Stack>
        {row.pagePath ? (
          <Typography variant="caption" color="text.secondary">
            Page: {row.pagePath}
          </Typography>
        ) : null}
        <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap', lineHeight: 1.6 }}>
          {row.message}
        </Typography>
        {row.ownerComment ? (
          <Box
            sx={(theme) => ({
              pl: 1.5,
              py: 1,
              borderLeft: 3,
              borderColor: 'primary.main',
              bgcolor:
                theme.palette.mode === 'dark' ? 'rgba(144, 202, 249, 0.08)' : 'action.hover',
              borderRadius: 1,
            })}
          >
            <Typography variant="caption" color="primary" fontWeight={600} display="block" gutterBottom>
              Group owner
              {row.ownerCommentAt
                ? ` · ${new Date(row.ownerCommentAt).toLocaleString()}`
                : ''}
            </Typography>
            <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>
              {row.ownerComment}
            </Typography>
          </Box>
        ) : null}
        {isOwner && (
          <>
            <Divider sx={{ my: 0.5 }} />
            <Stack spacing={1}>
              <TextField
                size="small"
                fullWidth
                label="Owner reply (visible to everyone on public items; visible only to the author on private items)"
                multiline
                minRows={2}
                value={commentDraft}
                onChange={(e) => onCommentDraft(e.target.value)}
                inputProps={{ maxLength: 4000 }}
                disabled={busy}
              />
              <Stack direction="row" gap={1} flexWrap="wrap" alignItems="center">
                <Button size="small" variant="outlined" disabled={busy} onClick={onSaveComment}>
                  Save reply
                </Button>
                {row.category !== 'general' && (
                  <ButtonGroup size="small" variant="outlined" disabled={busy}>
                    <Button
                      color={st === 'open' ? 'primary' : 'inherit'}
                      onClick={() => onSetStatus('open')}
                    >
                      Reopen
                    </Button>
                    <Button
                      color={st === 'resolved' ? 'success' : 'inherit'}
                      onClick={() => onSetStatus('resolved')}
                    >
                      Resolve
                    </Button>
                    <Button onClick={() => onSetStatus('ignored')}>Ignore</Button>
                  </ButtonGroup>
                )}
              </Stack>
            </Stack>
          </>
        )}
      </Stack>
    </Paper>
  );
}

export default function GroupFeedbackPage() {
  const { user } = useAuth();
  const { groupId } = useParams();
  const { pathname } = useLocation();
  const qc = useQueryClient();

  const meQ = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () => (await api.get(`/groups/${groupId}/me`)).data as GroupMe,
  });

  const isOwner = meQ.data?.role === 'creator';
  const [memberTab, setMemberTab] = useState(0);
  const [ownerMainTab, setOwnerMainTab] = useState(0);
  const [ownerStatus, setOwnerStatus] = useState<'all' | FeedbackStatus>('open');

  const ownerScope = ownerMainTab === 0 ? 'community' : 'private';

  const listQ = useQuery({
    queryKey: [
      'group',
      groupId,
      'feedback',
      isOwner ? ownerScope : 'member',
      isOwner && ownerScope === 'private' ? ownerStatus : '-',
    ],
    enabled: !!groupId && !!meQ.data?.isMember,
    queryFn: async (): Promise<FeedbackListResponse> => {
      if (isOwner) {
        const params = new URLSearchParams();
        params.set('scope', ownerScope === 'community' ? 'community' : 'private');
        if (ownerScope === 'private' && ownerStatus !== 'all') params.set('status', ownerStatus);
        const { data } = await api.get(`/groups/${groupId}/feedback?${params.toString()}`);
        return data as FeedbackListResponse;
      }
      const { data } = await api.get(`/groups/${groupId}/feedback`);
      return data as FeedbackListResponse;
    },
  });

  const [category, setCategory] = useState<FeedbackCategory>('general');
  const [message, setMessage] = useState('');
  const [submitted, setSubmitted] = useState(false);
  const submitMut = useMutation({
    mutationFn: async () =>
      api.post(`/groups/${groupId}/feedback`, {
        category,
        message: message.trim(),
        pagePath: pathname,
      }),
    onSuccess: () => {
      setMessage('');
      setSubmitted(true);
      qc.invalidateQueries({ queryKey: ['group', groupId, 'feedback'] });
    },
  });

  const patchMut = useMutation({
    mutationFn: async (args: { id: string; status?: FeedbackStatus; ownerComment?: string }) =>
      api.patch(`/groups/${groupId}/feedback/${args.id}`, {
        ...(args.status !== undefined ? { status: args.status } : {}),
        ...(args.ownerComment !== undefined ? { ownerComment: args.ownerComment } : {}),
      }),
    onSettled: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId, 'feedback'] });
    },
  });

  const memberSplit = useMemo(() => {
    const items = listQ.data?.feedback ?? [];
    return {
      general: items.filter((i) => i.category === 'general'),
      privateMine: items.filter((i) => i.category !== 'general'),
    };
  }, [listQ.data?.feedback]);

  if (!groupId) return null;

  if (meQ.isLoading) {
    return (
      <Box sx={{ py: 4 }}>
        <LinearProgress />
      </Box>
    );
  }

  if (meQ.isError || !meQ.data) {
    return <Alert severity="error">Could not load this group.</Alert>;
  }

  if (!meQ.data.isMember) {
    return (
      <Stack spacing={2} maxWidth={560}>
        <Typography variant="h5">Feedback</Typography>
        <Alert severity="info">
          Join this group to send feedback about your experience. Your notes help us improve DevStrider.
        </Alert>
      </Stack>
    );
  }

  const uid = user?.id ?? '';

  return (
    <Stack spacing={3} maxWidth={800}>
      <Box>
        <Typography variant="h5">Feedback</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 640 }}>
          <strong>General</strong> posts are visible to everyone in the group. <strong>Bug reports, ideas,</strong> and{' '}
          <strong>other</strong> notes are only visible to you and the group owner. The owner can reply, resolve, or
          ignore private reports.
        </Typography>
      </Box>

      {submitted && (
        <Alert severity="success" onClose={() => setSubmitted(false)}>
          Thanks — your feedback was sent.
        </Alert>
      )}

      {submitMut.isError && (
        <Alert severity="error">
          {(submitMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
            'Could not send feedback. Try again.'}
        </Alert>
      )}

      {patchMut.isError && (
        <Alert severity="error">
          {(patchMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
            'Could not update feedback.'}
        </Alert>
      )}

      <Paper
        elevation={0}
        sx={(t) => ({
          p: 2,
          borderRadius: 2,
          border: 1,
          borderColor: 'divider',
          bgcolor: t.palette.mode === 'dark' ? 'rgba(255,255,255,0.03)' : 'background.paper',
        })}
      >
        <Typography variant="subtitle1" fontWeight={600} gutterBottom>
          New feedback
        </Typography>
        <Stack spacing={2} component="form" onSubmit={(e) => e.preventDefault()}>
          <FormControl fullWidth size="small">
            <InputLabel id="feedback-category-label">Category</InputLabel>
            <Select
              labelId="feedback-category-label"
              label="Category"
              value={category}
              onChange={(e) => setCategory(e.target.value as FeedbackCategory)}
            >
              {(Object.keys(categoryLabels) as FeedbackCategory[]).map((key) => (
                <MenuItem key={key} value={key}>
                  {categoryLabels[key]}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <TextField
            label="Message"
            multiline
            minRows={4}
            fullWidth
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            placeholder="What should we know?"
            inputProps={{ maxLength: 8000 }}
            helperText={`${message.length} / 8000 · Captured page: ${pathname}`}
          />
          <Button
            variant="contained"
            disabled={!message.trim() || submitMut.isPending}
            onClick={() => submitMut.mutate()}
            sx={{ alignSelf: 'flex-start' }}
          >
            {submitMut.isPending ? 'Sending…' : 'Submit'}
          </Button>
        </Stack>
      </Paper>

      <Box>
        <Typography variant="subtitle1" fontWeight={600} sx={{ mb: 1 }}>
          {isOwner ? 'Inbox' : 'Activity'}
        </Typography>
        {listQ.isLoading && <LinearProgress sx={{ mb: 2 }} />}
        {listQ.isError && <Alert severity="warning">Could not load feedback.</Alert>}

        {!isOwner && (
          <>
            <Tabs
              value={memberTab}
              onChange={(_, v) => setMemberTab(v)}
              sx={{ borderBottom: 1, borderColor: 'divider', mb: 2 }}
            >
              <Tab label={`Community (${memberSplit.general.length})`} {...tabProps(0)} />
              <Tab label={`Your private (${memberSplit.privateMine.length})`} {...tabProps(1)} />
            </Tabs>
            {memberTab === 0 && (
              <Stack spacing={2}>
                {memberSplit.general.length === 0 ? (
                  <Typography color="text.secondary" variant="body2">
                    No public feedback yet. Be the first to share a general note.
                  </Typography>
                ) : (
                  memberSplit.general.map((row) => (
                    <FeedbackCard
                      key={row._id}
                      row={row}
                      showAuthor
                      showStatus={false}
                      isOwner={false}
                      currentUserId={uid}
                      commentDraft=""
                      onCommentDraft={() => {}}
                      onSaveComment={() => {}}
                      onSetStatus={() => {}}
                      busy={false}
                    />
                  ))
                )}
              </Stack>
            )}
            {memberTab === 1 && (
              <Stack spacing={2}>
                {memberSplit.privateMine.length === 0 ? (
                  <Typography color="text.secondary" variant="body2">
                    You have no bug reports or ideas yet. Submit one above (choose Bug, Feature idea, or Other) to
                    track it here.
                  </Typography>
                ) : (
                  memberSplit.privateMine.map((row) => (
                    <FeedbackCard
                      key={row._id}
                      row={row}
                      showAuthor={false}
                      showStatus
                      isOwner={false}
                      currentUserId={uid}
                      commentDraft=""
                      onCommentDraft={() => {}}
                      onSaveComment={() => {}}
                      onSetStatus={() => {}}
                      busy={false}
                    />
                  ))
                )}
              </Stack>
            )}
          </>
        )}

        {isOwner && (
          <>
            <Tabs
              value={ownerMainTab}
              onChange={(_, v) => setOwnerMainTab(v)}
              sx={{ borderBottom: 1, borderColor: 'divider', mb: 2 }}
            >
              <Tab label="Community (general)" {...tabProps(0)} />
              <Tab label="Private reports" {...tabProps(1)} />
            </Tabs>
            {ownerMainTab === 1 && (
              <Stack direction="row" gap={1} flexWrap="wrap" alignItems="center" sx={{ mb: 2 }}>
                <Typography variant="caption" color="text.secondary">
                  Status:
                </Typography>
                <ButtonGroup size="small" variant="outlined">
                  <Button
                    variant={ownerStatus === 'open' ? 'contained' : 'outlined'}
                    onClick={() => setOwnerStatus('open')}
                  >
                    Open
                  </Button>
                  <Button
                    variant={ownerStatus === 'resolved' ? 'contained' : 'outlined'}
                    onClick={() => setOwnerStatus('resolved')}
                  >
                    Resolved
                  </Button>
                  <Button
                    variant={ownerStatus === 'ignored' ? 'contained' : 'outlined'}
                    onClick={() => setOwnerStatus('ignored')}
                  >
                    Ignored
                  </Button>
                  <Button
                    variant={ownerStatus === 'all' ? 'contained' : 'outlined'}
                    onClick={() => setOwnerStatus('all')}
                  >
                    All
                  </Button>
                </ButtonGroup>
              </Stack>
            )}
            <Stack spacing={2}>
              {listQ.data?.feedback.length === 0 && !listQ.isLoading ? (
                <Typography color="text.secondary" variant="body2">
                  Nothing here yet.
                </Typography>
              ) : (
                listQ.data?.feedback.map((row) => (
                  <OwnerFeedbackRow
                    key={row._id}
                    row={row}
                    currentUserId={uid}
                    patchMut={patchMut}
                  />
                ))
              )}
            </Stack>
          </>
        )}
      </Box>
    </Stack>
  );
}
