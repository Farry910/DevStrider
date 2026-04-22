import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  LinearProgress,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material';
import api from '../api/client';
import { isAxiosError } from 'axios';

type PopulatedUser = {
  _id: string;
  email?: string;
  nickname?: string;
};

type PendingRow = {
  _id: string;
  userId: PopulatedUser;
};

export default function GroupJoinRequestsPage() {
  const { groupId } = useParams();
  const qc = useQueryClient();

  const meQ = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/me`)).data as {
        group: { name: string };
        role: 'creator' | 'member' | 'none';
        isMember: boolean;
      },
  });

  const pendingQ = useQuery({
    queryKey: ['group', groupId, 'pending-requests'],
    enabled: !!groupId && meQ.data?.role === 'creator',
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/pending-requests`)).data as {
        pending: PendingRow[];
      },
  });

  const approveMut = useMutation({
    mutationFn: async (requestId: string) =>
      api.post(`/groups/${groupId}/join-requests/${requestId}/approve`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId, 'pending-requests'] });
      qc.invalidateQueries({ queryKey: ['groups'] });
    },
  });

  const rejectMut = useMutation({
    mutationFn: async (requestId: string) =>
      api.post(`/groups/${groupId}/join-requests/${requestId}/reject`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId, 'pending-requests'] });
    },
  });

  if (!groupId) return null;

  if (meQ.isLoading) {
    return (
      <Box sx={{ py: 4 }}>
        <LinearProgress />
      </Box>
    );
  }

  if (meQ.data?.role !== 'creator') {
    return (
      <Alert severity="warning">
        Only the group creator can view and approve join requests. Ask the creator of{' '}
        <strong>{meQ.data?.group?.name ?? 'this group'}</strong> to approve members.
      </Alert>
    );
  }

  const pending = pendingQ.data?.pending ?? [];

  return (
    <Stack spacing={2}>
      <Box>
        <Typography variant="h5">Join requests</Typography>
        <Typography color="text.secondary" variant="body2">
          Approve or reject people who asked to join <strong>{meQ.data?.group?.name}</strong>. When you
          approve, they become members and can use the bid board and interviews.
        </Typography>
      </Box>

      {pendingQ.isError && (
        <Alert severity="error">
          {isAxiosError(pendingQ.error) &&
          pendingQ.error.response?.data &&
          typeof (pendingQ.error.response.data as { error?: unknown }).error === 'string'
            ? (pendingQ.error.response.data as { error: string }).error
            : 'Could not load pending requests.'}
        </Alert>
      )}

      {(approveMut.isError || rejectMut.isError) && (
        <Alert severity="error">Could not update that request. Try again.</Alert>
      )}

      {pendingQ.isLoading && <LinearProgress />}

      {!pendingQ.isLoading && pending.length === 0 && (
        <Alert severity="info">No pending join requests right now.</Alert>
      )}

      {pending.length > 0 && (
        <Paper variant="outlined" sx={{ overflow: 'auto' }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Nickname</TableCell>
                <TableCell>Email</TableCell>
                <TableCell align="right">Actions</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {pending.map((row) => {
                const u = row.userId;
                const label = u?.nickname?.trim() || u?.email || u?._id || '—';
                return (
                  <TableRow key={row._id}>
                    <TableCell>{label}</TableCell>
                    <TableCell>{u?.email ?? '—'}</TableCell>
                    <TableCell align="right">
                      <Stack direction="row" spacing={1} justifyContent="flex-end">
                        <Button
                          size="small"
                          variant="contained"
                          color="success"
                          disabled={approveMut.isPending || rejectMut.isPending}
                          onClick={() => approveMut.mutate(row._id)}
                        >
                          Approve
                        </Button>
                        <Button
                          size="small"
                          variant="outlined"
                          color="inherit"
                          disabled={approveMut.isPending || rejectMut.isPending}
                          onClick={() => rejectMut.mutate(row._id)}
                        >
                          Reject
                        </Button>
                      </Stack>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </Paper>
      )}
    </Stack>
  );
}
