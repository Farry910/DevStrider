import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Button,
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
import { useState } from 'react';
import { Navigate, useParams } from 'react-router-dom';
import api from '../api/client';

type PendingRow = {
  id: string;
  badgeKey: string;
  badgeType: { key: string; label: string; shortLabel: string; color: string } | null;
  createdAt: string;
  user: { id: string; email: string; nickname: string } | null;
};

export default function GroupProfileBadgeRequestsPage() {
  const { groupId } = useParams();
  const qc = useQueryClient();
  const [rejectNoteById, setRejectNoteById] = useState<Record<string, string>>({});

  const meQ = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () => (await api.get(`/groups/${groupId}/me`)).data as { role: string },
  });

  const q = useQuery({
    queryKey: ['group', groupId, 'pending-profile-badge-requests'],
    enabled: !!groupId && meQ.data?.role === 'creator',
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/pending-profile-badge-requests`)).data as { pending: PendingRow[] },
  });

  const approveMut = useMutation({
    mutationFn: async (id: string) => {
      await api.post(`/groups/${groupId}/profile-badge-requests/${id}/approve`);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId, 'pending-profile-badge-requests'] });
      qc.invalidateQueries({ queryKey: ['group', groupId, 'profile-badges-summary'] });
    },
  });

  const rejectMut = useMutation({
    mutationFn: async ({ id, note }: { id: string; note: string }) => {
      await api.post(`/groups/${groupId}/profile-badge-requests/${id}/reject`, {
        note: note.trim() || undefined,
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId, 'pending-profile-badge-requests'] });
      qc.invalidateQueries({ queryKey: ['group', groupId, 'profile-badges-summary'] });
    },
  });

  if (!groupId) {
    return <Navigate to="/" replace />;
  }

  if (meQ.isSuccess && meQ.data.role !== 'creator') {
    return <Navigate to={`/g/${groupId}`} replace />;
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h5">Badge requests</Typography>
      <Typography variant="body2" color="text.secondary">
        Members can request profile badges for this group. Approve or reject each request. Approved badges show on job
        links they created here.
      </Typography>

      {(meQ.isLoading || q.isLoading) && <LinearProgress />}

      <Paper variant="outlined" sx={{ overflow: 'auto' }}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Member</TableCell>
              <TableCell>Badge</TableCell>
              <TableCell>Requested</TableCell>
              <TableCell align="right">Actions</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {(q.data?.pending ?? []).length === 0 ? (
              <TableRow>
                <TableCell colSpan={4}>
                  <Typography variant="body2" color="text.secondary" sx={{ py: 2 }}>
                    No pending badge requests.
                  </Typography>
                </TableCell>
              </TableRow>
            ) : (
              q.data!.pending.map((row) => (
                <TableRow key={row.id} hover>
                  <TableCell>
                    <Typography variant="body2">{row.user?.nickname ?? '—'}</Typography>
                    <Typography variant="caption" color="text.secondary" display="block">
                      {row.user?.email ?? ''}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2" fontWeight={600}>
                      {row.badgeType?.label ?? row.badgeKey}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="caption" color="text.secondary">
                      {new Date(row.createdAt).toLocaleString()}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Stack direction="column" spacing={1} alignItems="flex-end" sx={{ minWidth: 200 }}>
                      <Stack direction="row" spacing={0.5}>
                        <Button
                          size="small"
                          color="success"
                          variant="contained"
                          disabled={approveMut.isPending || rejectMut.isPending}
                          onClick={() => approveMut.mutate(row.id)}
                        >
                          Approve
                        </Button>
                        <Button
                          size="small"
                          color="error"
                          variant="outlined"
                          disabled={approveMut.isPending || rejectMut.isPending}
                          onClick={() =>
                            rejectMut.mutate({
                              id: row.id,
                              note: rejectNoteById[row.id] ?? '',
                            })
                          }
                        >
                          Reject
                        </Button>
                      </Stack>
                      <TextField
                        size="small"
                        placeholder="Optional note to member"
                        value={rejectNoteById[row.id] ?? ''}
                        onChange={(e) =>
                          setRejectNoteById((prev) => ({ ...prev, [row.id]: e.target.value }))
                        }
                        fullWidth
                      />
                    </Stack>
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </Paper>
    </Stack>
  );
}
