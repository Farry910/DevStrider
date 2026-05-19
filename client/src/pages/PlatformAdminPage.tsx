import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  InputAdornment,
  LinearProgress,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import LockResetIcon from '@mui/icons-material/LockReset';
import VisibilityIcon from '@mui/icons-material/Visibility';
import VisibilityOffIcon from '@mui/icons-material/VisibilityOff';
import api from '../api/client';
import { useAuth } from '../auth/AuthContext';

type PendingGroup = {
  id: string;
  name: string;
  locationKey: string;
  createdAt: string;
  creator: { id: string; email: string; nickname: string } | null;
};

type StorageCollection = { name: string; bytes: number; count: number };
type StorageResponse = {
  collections: StorageCollection[];
  totalBytes: number;
  totalCount: number;
  groupCount: number;
};

type GroupSummary = {
  _id: string;
  name: string;
  locationKey: string;
  creatorId: string;
  memberIds?: string[];
};

type AdminUserRow = {
  id: string;
  email: string;
  nickname: string;
  platformRole: 'admin' | 'user';
  createdAt: string;
};

function fmtBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
  if (b < 1024 * 1024 * 1024) return `${(b / (1024 * 1024)).toFixed(1)} MB`;
  return `${(b / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

export default function PlatformAdminPage() {
  const { user } = useAuth();
  const qc = useQueryClient();
  const isAdmin = user?.platformRole === 'admin';

  const pendingQ = useQuery({
    queryKey: ['admin', 'pending-groups'] as const,
    enabled: isAdmin,
    queryFn: async () =>
      (await api.get('/admin/pending-groups')).data as { groups: PendingGroup[] },
  });

  const storageQ = useQuery({
    queryKey: ['admin', 'storage'] as const,
    enabled: isAdmin,
    queryFn: async () => (await api.get('/admin/storage')).data as StorageResponse,
  });

  const allGroupsQ = useQuery({
    queryKey: ['admin', 'all-groups'] as const,
    enabled: isAdmin,
    queryFn: async () => (await api.get('/groups/all')).data as { groups: GroupSummary[] },
  });

  const approveMut = useMutation({
    mutationFn: async (groupId: string) => api.post(`/admin/groups/${groupId}/approve`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['admin', 'pending-groups'] });
      qc.invalidateQueries({ queryKey: ['admin', 'all-groups'] });
    },
  });

  const rejectMut = useMutation({
    mutationFn: async (groupId: string) => api.post(`/admin/groups/${groupId}/reject`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'pending-groups'] }),
  });

  const [transferTarget, setTransferTarget] = useState<GroupSummary | null>(null);
  const [newOwnerId, setNewOwnerId] = useState<string>('');

  const transferGroupMembersQ = useQuery({
    queryKey: ['admin', 'transfer-members', transferTarget?._id] as const,
    enabled: isAdmin && !!transferTarget,
    queryFn: async () =>
      (await api.get(`/groups/${transferTarget!._id}/members-detailed`)).data as {
        members: Array<{ userId: string; nickname: string; email: string; isCreator: boolean }>;
      },
  });

  const transferMut = useMutation({
    mutationFn: async () =>
      api.post(`/admin/groups/${transferTarget!._id}/transfer-ownership`, {
        newOwnerId,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['admin', 'all-groups'] });
      qc.invalidateQueries({
        queryKey: ['admin', 'transfer-members', transferTarget?._id],
      });
      setTransferTarget(null);
      setNewOwnerId('');
    },
  });

  /** Users list + password-reset dialog state. */
  const [userSearch, setUserSearch] = useState('');
  const [resetTarget, setResetTarget] = useState<AdminUserRow | null>(null);
  const [resetPassword, setResetPassword] = useState('');
  const [resetShowPassword, setResetShowPassword] = useState(false);
  const [resetSuccessEmail, setResetSuccessEmail] = useState<string | null>(null);

  const usersQ = useQuery({
    queryKey: ['admin', 'users', userSearch] as const,
    enabled: isAdmin,
    queryFn: async () =>
      (
        await api.get('/admin/users', {
          params: userSearch.trim() ? { search: userSearch.trim() } : {},
        })
      ).data as { users: AdminUserRow[] },
  });

  const resetPasswordMut = useMutation({
    mutationFn: async () =>
      api.post(`/admin/users/${resetTarget!.id}/reset-password`, {
        newPassword: resetPassword,
      }),
    onSuccess: () => {
      setResetSuccessEmail(resetTarget?.email || '');
      setResetTarget(null);
      setResetPassword('');
      setResetShowPassword(false);
    },
  });

  if (!isAdmin) {
    return <Alert severity="error">Platform admin only.</Alert>;
  }

  return (
    <Stack spacing={3} maxWidth={960}>
      <Typography variant="h5">Platform admin</Typography>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" gutterBottom>
          Pending group approvals
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Groups submitted by users wait here until you approve them. Rejecting a pending group
          deletes it (nothing else has attached yet, so cleanup is automatic).
        </Typography>
        {pendingQ.isLoading && <LinearProgress />}
        {pendingQ.isError && <Alert severity="error">Could not load pending list.</Alert>}
        {pendingQ.data && pendingQ.data.groups.length === 0 && (
          <Typography variant="caption" color="text.secondary">
            No groups pending.
          </Typography>
        )}
        <Stack spacing={1}>
          {pendingQ.data?.groups.map((g) => (
            <Box
              key={g.id}
              sx={{
                p: 1.25,
                border: 1,
                borderColor: 'divider',
                borderRadius: 1,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
                gap: 1,
              }}
            >
              <Box sx={{ minWidth: 0 }}>
                <Typography variant="body2" fontWeight={600}>
                  {g.name}
                </Typography>
                <Typography variant="caption" color="text.secondary">
                  {g.locationKey} · created {new Date(g.createdAt).toLocaleString()} ·{' '}
                  by {g.creator?.nickname || g.creator?.email || '—'}
                </Typography>
              </Box>
              <Stack direction="row" spacing={1}>
                <Button
                  size="small"
                  variant="contained"
                  disabled={approveMut.isPending}
                  onClick={() => approveMut.mutate(g.id)}
                >
                  Approve
                </Button>
                <Button
                  size="small"
                  variant="outlined"
                  color="error"
                  disabled={rejectMut.isPending}
                  onClick={() => {
                    if (
                      !window.confirm(
                        `Reject "${g.name}"? The pending group will be deleted permanently.`
                      )
                    ) {
                      return;
                    }
                    rejectMut.mutate(g.id);
                  }}
                >
                  Reject
                </Button>
              </Stack>
            </Box>
          ))}
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" gutterBottom>
          Transfer group ownership
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Reassign a group's owner. The new owner must already be a member. The previous owner
          becomes OPS by default.
        </Typography>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <Autocomplete
            options={allGroupsQ.data?.groups ?? []}
            getOptionLabel={(g) => `${g.name} (${g.locationKey})`}
            isOptionEqualToValue={(a, b) => a._id === b._id}
            value={transferTarget}
            onChange={(_, v) => {
              setTransferTarget(v);
              setNewOwnerId('');
            }}
            sx={{ flex: 1, minWidth: 0 }}
            renderInput={(params) => <TextField {...params} size="small" label="Group" />}
          />
          <Autocomplete
            options={
              (transferGroupMembersQ.data?.members ?? []).filter((m) => !m.isCreator)
            }
            getOptionLabel={(m) => `${m.nickname || m.email}`}
            isOptionEqualToValue={(a, b) => a.userId === b.userId}
            value={
              (transferGroupMembersQ.data?.members ?? []).find((m) => m.userId === newOwnerId) ||
              null
            }
            onChange={(_, v) => setNewOwnerId(v?.userId || '')}
            disabled={!transferTarget}
            sx={{ flex: 1, minWidth: 0 }}
            renderInput={(params) => <TextField {...params} size="small" label="New owner" />}
          />
          <Button
            variant="contained"
            disabled={!transferTarget || !newOwnerId || transferMut.isPending}
            onClick={() => {
              if (
                !window.confirm(
                  `Transfer "${transferTarget!.name}" to the selected member? The current owner will be demoted to OPS.`
                )
              ) {
                return;
              }
              transferMut.mutate();
            }}
          >
            Transfer
          </Button>
        </Stack>
        {transferMut.isError && (
          <Alert severity="error" sx={{ mt: 1 }}>
            {(transferMut.error as { response?: { data?: { error?: string } } })?.response?.data
              ?.error ?? 'Transfer failed.'}
          </Alert>
        )}
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
          <Typography variant="subtitle1">Cross-group storage</Typography>
          <Chip
            size="small"
            label={`${storageQ.data?.groupCount ?? '…'} groups`}
            variant="outlined"
          />
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Total bytes per collection across every group. Indexes are not counted.
        </Typography>
        {storageQ.isLoading && <LinearProgress />}
        {storageQ.isError && <Alert severity="error">Could not load storage.</Alert>}
        {storageQ.data && (
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Collection</TableCell>
                <TableCell align="right">Rows</TableCell>
                <TableCell align="right">Size</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {storageQ.data.collections.map((c) => (
                <TableRow key={c.name}>
                  <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8125rem' }}>
                    {c.name}
                  </TableCell>
                  <TableCell align="right">{c.count.toLocaleString()}</TableCell>
                  <TableCell align="right">{fmtBytes(c.bytes)}</TableCell>
                </TableRow>
              ))}
              <TableRow sx={{ '& td': { fontWeight: 600 } }}>
                <TableCell>Total</TableCell>
                <TableCell align="right">{storageQ.data.totalCount.toLocaleString()}</TableCell>
                <TableCell align="right">{fmtBytes(storageQ.data.totalBytes)}</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        )}
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
          <Typography variant="subtitle1">Users &amp; password reset</Typography>
          <TextField
            size="small"
            value={userSearch}
            onChange={(e) => setUserSearch(e.target.value)}
            placeholder="Search email or nickname"
            sx={{ minWidth: 260 }}
          />
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Resets a user's password to a value you set. They'll need to log in with the new one.
          List is capped at 200; refine with search if needed.
        </Typography>
        {usersQ.isLoading && <LinearProgress />}
        {usersQ.isError && <Alert severity="error">Could not load users.</Alert>}
        {resetSuccessEmail && (
          <Alert severity="success" sx={{ mb: 1 }} onClose={() => setResetSuccessEmail(null)}>
            Password reset for {resetSuccessEmail}.
          </Alert>
        )}
        {usersQ.data && (
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Email</TableCell>
                <TableCell>Nickname</TableCell>
                <TableCell>Role</TableCell>
                <TableCell>Joined</TableCell>
                <TableCell align="right">Action</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {usersQ.data.users.map((u) => (
                <TableRow key={u.id} hover>
                  <TableCell sx={{ wordBreak: 'break-all' }}>{u.email}</TableCell>
                  <TableCell>{u.nickname}</TableCell>
                  <TableCell>
                    {u.platformRole === 'admin' ? (
                      <Chip size="small" color="primary" label="platform admin" />
                    ) : (
                      <Typography variant="caption" color="text.secondary">
                        user
                      </Typography>
                    )}
                  </TableCell>
                  <TableCell>
                    <Typography variant="caption" color="text.secondary">
                      {new Date(u.createdAt).toLocaleDateString()}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Tooltip title="Reset password">
                      <IconButton
                        size="small"
                        aria-label={`Reset password for ${u.email}`}
                        onClick={() => {
                          setResetTarget(u);
                          setResetPassword('');
                          setResetShowPassword(false);
                          setResetSuccessEmail(null);
                        }}
                      >
                        <LockResetIcon fontSize="small" />
                      </IconButton>
                    </Tooltip>
                  </TableCell>
                </TableRow>
              ))}
              {usersQ.data.users.length === 0 && (
                <TableRow>
                  <TableCell colSpan={5}>
                    <Typography variant="caption" color="text.secondary">
                      No users match this search.
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        )}
      </Paper>

      <Dialog
        open={Boolean(resetTarget)}
        onClose={() => setResetTarget(null)}
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle>Reset password</DialogTitle>
        <DialogContent>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Setting a new password for <strong>{resetTarget?.email}</strong>. The user will need to
            sign in again. 8–128 characters.
          </Typography>
          <TextField
            autoFocus
            fullWidth
            size="small"
            label="New password"
            type={resetShowPassword ? 'text' : 'password'}
            value={resetPassword}
            onChange={(e) => setResetPassword(e.target.value)}
            InputProps={{
              endAdornment: (
                <InputAdornment position="end">
                  <IconButton
                    size="small"
                    onClick={() => setResetShowPassword((v) => !v)}
                    aria-label={resetShowPassword ? 'Hide password' : 'Show password'}
                  >
                    {resetShowPassword ? (
                      <VisibilityOffIcon fontSize="small" />
                    ) : (
                      <VisibilityIcon fontSize="small" />
                    )}
                  </IconButton>
                </InputAdornment>
              ),
            }}
          />
          {resetPasswordMut.isError && (
            <Alert severity="error" sx={{ mt: 1 }}>
              {(resetPasswordMut.error as { response?: { data?: { error?: string } } })
                ?.response?.data?.error ?? 'Reset failed.'}
            </Alert>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setResetTarget(null)}>Cancel</Button>
          <Button
            variant="contained"
            color="error"
            disabled={
              resetPasswordMut.isPending ||
              resetPassword.length < 8 ||
              resetPassword.length > 128
            }
            onClick={() => resetPasswordMut.mutate()}
          >
            {resetPasswordMut.isPending ? 'Resetting…' : 'Reset password'}
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  );
}
