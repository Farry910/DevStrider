import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Chip,
  LinearProgress,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
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
    </Stack>
  );
}
