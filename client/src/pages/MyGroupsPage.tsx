import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box,
  Button,
  Card,
  CardContent,
  Divider,
  Grid,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import api from '../api/client';
import { useAuth } from '../auth/AuthContext';

type Group = {
  _id: string;
  name: string;
  locationKey: string;
  creatorId: string;
  memberIds: string[];
};

/** Personal dashboard: your groups, create, browse (no group context). */
export default function MyGroupsPage() {
  const { user } = useAuth();
  const nav = useNavigate();
  const qc = useQueryClient();

  const { data: mine } = useQuery({
    queryKey: ['groups', 'mine'],
    queryFn: async () => (await api.get<{ groups: Group[] }>('/groups')).data,
  });

  const { data: all } = useQuery({
    queryKey: ['groups', 'all'],
    queryFn: async () => (await api.get<{ groups: Group[] }>('/groups/all')).data,
  });

  const [newName, setNewName] = useState('');
  const [newLoc, setNewLoc] = useState('');

  const createMut = useMutation({
    mutationFn: async () =>
      api.post('/groups', { name: newName, locationKey: newLoc }),
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['groups'] });
      nav(`/g/${(res.data as { group: Group }).group._id}/bids`);
    },
  });

  return (
    <Stack spacing={3}>
      <Box>
        <Typography variant="h5">My groups</Typography>
        <Typography color="text.secondary" variant="body2">
          Join a location group to share links and compare progress. Use the sidebar when you open a
          group.
        </Typography>
      </Box>
      <Grid container spacing={2}>
        {mine?.groups?.map((g) => (
          <Grid item xs={12} md={4} key={g._id}>
            <Card variant="outlined">
              <CardContent>
                <Typography variant="h6">{g.name}</Typography>
                <Typography color="text.secondary" variant="caption" display="block">
                  {g.locationKey}
                  {user && String(g.creatorId) === user.id ? ' · You own this group' : ''}
                </Typography>
                <Stack direction="row" spacing={1} sx={{ mt: 2 }} flexWrap="wrap" useFlexGap>
                  <Button size="small" variant="contained" onClick={() => nav(`/g/${g._id}/bids`)}>
                    Open workspace
                  </Button>
                  {user && String(g.creatorId) === user.id && (
                    <Button size="small" variant="outlined" onClick={() => nav(`/g/${g._id}/settings`)}>
                      Settings
                    </Button>
                  )}
                </Stack>
              </CardContent>
            </Card>
          </Grid>
        ))}
      </Grid>
      <Divider />
      <Typography variant="h6">Create a group</Typography>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} maxWidth={720}>
        <TextField
          label="Group name"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          fullWidth
        />
        <TextField
          label="Location key (e.g. us, mexico)"
          value={newLoc}
          onChange={(e) => setNewLoc(e.target.value)}
          fullWidth
        />
        <Button
          onClick={() => createMut.mutate()}
          disabled={!newName.trim() || !newLoc.trim() || createMut.isPending}
        >
          Create
        </Button>
      </Stack>
      <Divider />
      <Typography variant="h6">Browse groups</Typography>
      <Grid container spacing={2}>
        {all?.groups?.map((g) => (
          <Grid item xs={12} md={4} key={g._id}>
            <Card variant="outlined">
              <CardContent>
                <Typography variant="subtitle1">{g.name}</Typography>
                <Typography color="text.secondary" variant="caption" display="block">
                  {g.locationKey}
                </Typography>
                <Button size="small" sx={{ mt: 1 }} onClick={() => nav(`/g/${g._id}`)}>
                  View / join
                </Button>
              </CardContent>
            </Card>
          </Grid>
        ))}
      </Grid>
    </Stack>
  );
}
