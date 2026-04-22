import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Navigate, useParams } from 'react-router-dom';
import { Alert, Box, Button, LinearProgress, Stack, Typography } from '@mui/material';
import api from '../api/client';

type Group = {
  _id: string;
  name: string;
  locationKey: string;
};

/** Members go straight to the bid board; others see a compact join prompt. */
export default function GroupLandingPage() {
  const { groupId } = useParams();
  const qc = useQueryClient();

  const q = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/me`)).data as {
        group: Group;
        isMember: boolean;
        role: 'creator' | 'member' | 'none';
      },
  });

  const joinMut = useMutation({
    mutationFn: async () => api.post(`/groups/${groupId}/join-request`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['group', groupId, 'me'] }),
  });

  if (!groupId) return null;

  if (q.isLoading) {
    return (
      <Box sx={{ py: 4 }}>
        <LinearProgress />
      </Box>
    );
  }

  if (q.isError) {
    return (
      <Alert severity="error">
        Could not load this group. It may not exist or you may have lost access.
      </Alert>
    );
  }

  if (q.data?.isMember) {
    return <Navigate to={`/g/${groupId}/bids`} replace />;
  }

  return (
    <Stack spacing={2} maxWidth={480}>
      <Typography variant="h5">{q.data?.group?.name ?? 'Group'}</Typography>
      <Typography color="text.secondary" variant="body2">
        You are not a member yet. Request to join to use the shared bid board and interviews.
      </Typography>
      <Alert severity="info">
        <Box sx={{ mt: 1 }}>
          <Button
            size="small"
            variant="contained"
            onClick={() => joinMut.mutate()}
            disabled={joinMut.isPending}
          >
            Request to join
          </Button>
        </Box>
      </Alert>
    </Stack>
  );
}
