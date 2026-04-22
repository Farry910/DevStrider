import { useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Navigate, useParams } from 'react-router-dom';
import {
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
  Typography,
} from '@mui/material';
import api from '../api/client';

type BadgeType = {
  key: string;
  label: string;
  shortLabel: string;
  color: string;
};

type BadgeRequestRow = {
  id: string;
  badgeKey: string;
  badgeType: BadgeType | null;
  status: string;
  createdAt: string;
  updatedAt: string;
  reviewedAt: string | null;
  reviewNote: string;
};

function latestRequestForKey(requests: BadgeRequestRow[], key: string): BadgeRequestRow | undefined {
  return requests
    .filter((r) => r.badgeKey === key)
    .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime())[0];
}

export default function ProfileBadgesPage() {
  const { groupId } = useParams();
  const qc = useQueryClient();

  const summaryQ = useQuery({
    queryKey: ['group', groupId, 'profile-badges-summary'],
    enabled: !!groupId,
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/me/profile-badges-summary`)).data as {
        approvedKeys: string[];
        requests: BadgeRequestRow[];
      },
  });

  const typesQ = useQuery({
    queryKey: ['badge-types'],
    queryFn: async () => (await api.get('/badge-types')).data as { badgeTypes: BadgeType[] },
  });

  const requestMut = useMutation({
    mutationFn: async (badgeKey: string) => {
      await api.post(`/groups/${groupId}/me/profile-badge-requests`, { badgeKey });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId, 'profile-badges-summary'] });
    },
  });

  const approved = useMemo(
    () => new Set(summaryQ.data?.approvedKeys ?? []),
    [summaryQ.data?.approvedKeys]
  );

  const rows = useMemo(() => {
    const types = typesQ.data?.badgeTypes ?? [];
    const requests = summaryQ.data?.requests ?? [];
    return types.map((t) => {
      const has = approved.has(t.key);
      const latest = latestRequestForKey(requests, t.key);
      let state: 'approved' | 'pending' | 'rejected' | 'none' = 'none';
      if (has) state = 'approved';
      else if (latest?.status === 'pending') state = 'pending';
      else if (latest?.status === 'rejected') state = 'rejected';
      return { type: t, state, latest };
    });
  }, [typesQ.data, summaryQ.data, approved]);

  if (!groupId) {
    return <Navigate to="/" replace />;
  }

  return (
    <Stack spacing={2} sx={{ maxWidth: 720 }}>
      <Typography variant="h5">Profile badges</Typography>
      <Typography variant="body2" color="text.secondary">
        Choose badge types for this group. The group owner reviews requests. Approved badges appear next to your name
        on job links you add in this group.
      </Typography>

      {(typesQ.isLoading || summaryQ.isLoading) && <LinearProgress />}
      {summaryQ.isError && (
        <Typography color="error" variant="body2">
          {(summaryQ.error as Error)?.message || 'Could not load badge status. Are you a member of this group?'}
        </Typography>
      )}

      <Paper variant="outlined" sx={{ overflow: 'auto' }}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Badge</TableCell>
              <TableCell>Status</TableCell>
              <TableCell align="right">Action</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {rows.map(({ type: t, state, latest }) => (
              <TableRow key={t.key} hover>
                <TableCell>
                  <Stack direction="row" alignItems="center" spacing={1}>
                    <Box
                      sx={{
                        width: 10,
                        height: 28,
                        borderRadius: 0.5,
                        bgcolor: t.color,
                        flexShrink: 0,
                      }}
                    />
                    <Box>
                      <Typography variant="body2" fontWeight={600}>
                        {t.label}
                      </Typography>
                      <Typography variant="caption" color="text.secondary">
                        {t.key}
                      </Typography>
                    </Box>
                  </Stack>
                </TableCell>
                <TableCell>
                  {state === 'approved' && <Chip size="small" color="success" label="Approved" />}
                  {state === 'pending' && <Chip size="small" color="warning" label="Pending review" />}
                  {state === 'rejected' && (
                    <Stack spacing={0.25}>
                      <Chip size="small" color="error" label="Not approved" />
                      {latest?.reviewNote ? (
                        <Typography variant="caption" color="text.secondary">
                          {latest.reviewNote}
                        </Typography>
                      ) : null}
                    </Stack>
                  )}
                  {state === 'none' && (
                    <Typography variant="caption" color="text.secondary">
                      Not requested
                    </Typography>
                  )}
                </TableCell>
                <TableCell align="right">
                  {state === 'none' || state === 'rejected' ? (
                    <Button
                      size="small"
                      variant="outlined"
                      disabled={requestMut.isPending || summaryQ.isError}
                      onClick={() => requestMut.mutate(t.key)}
                    >
                      Request
                    </Button>
                  ) : (
                    <Typography variant="caption" color="text.secondary">
                      —
                    </Typography>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Paper>
    </Stack>
  );
}
