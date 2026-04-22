import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  LinearProgress,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import api from '../api/client';
import { useAuth } from '../auth/AuthContext';

type GroupTimers = {
  junkRemovalGraceMinutes: number;
  bidDuplicateLookbackDays: number;
  possibleTimerMinutes: number;
};

type GroupMe = {
  group: {
    _id: string;
    name: string;
    locationKey: string;
    removalAssisterId?: string | null;
    removalOwnerConfirmedAt?: string | null;
    removalAssisterConfirmedAt?: string | null;
    timers?: GroupTimers;
  };
  role: 'creator' | 'member' | 'none';
  isMember: boolean;
  removal: {
    assisterUserId: string | null;
    ownerConfirmedAt: string | null;
    assisterConfirmedAt: string | null;
  };
};

type MemberUser = { _id: string; nickname: string; email: string };

export default function GroupSettingsPage() {
  const { user } = useAuth();
  const { groupId } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [name, setName] = useState('');
  const [locationKey, setLocationKey] = useState('');
  const [junkRemovalGraceMinutes, setJunkRemovalGraceMinutes] = useState(10);
  const [bidDuplicateLookbackDays, setBidDuplicateLookbackDays] = useState(365);
  const [possibleTimerMinutes, setPossibleTimerMinutes] = useState(0);

  const meQ = useQuery({
    queryKey: ['group', groupId, 'me'],
    enabled: !!groupId,
    queryFn: async () => (await api.get(`/groups/${groupId}/me`)).data as GroupMe,
  });

  const membersQ = useQuery({
    queryKey: ['group-members', groupId],
    enabled: !!groupId && meQ.data?.role === 'creator',
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/members`)).data as { users: MemberUser[] },
  });

  const [assisterPick, setAssisterPick] = useState<MemberUser | null>(null);

  useEffect(() => {
    const g = meQ.data?.group;
    if (g) {
      setName(g.name);
      setLocationKey(g.locationKey);
      if (g.timers) {
        setJunkRemovalGraceMinutes(g.timers.junkRemovalGraceMinutes);
        setBidDuplicateLookbackDays(g.timers.bidDuplicateLookbackDays);
        setPossibleTimerMinutes(g.timers.possibleTimerMinutes ?? 0);
      }
    }
  }, [meQ.data?.group]);

  useEffect(() => {
    const aid = meQ.data?.removal?.assisterUserId;
    const users = membersQ.data?.users;
    if (!aid || !users) {
      setAssisterPick(null);
      return;
    }
    setAssisterPick(users.find((u) => u._id === aid) ?? null);
  }, [meQ.data?.removal?.assisterUserId, membersQ.data?.users]);

  const patchMut = useMutation({
    mutationFn: async () =>
      api.patch(`/groups/${groupId}`, {
        name: name.trim(),
        locationKey: locationKey.trim().toLowerCase(),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId] });
      qc.invalidateQueries({ queryKey: ['groups'] });
    },
  });

  const assisterMut = useMutation({
    mutationFn: async (userId: string | null) =>
      api.patch(`/groups/${groupId}/removal-assister`, { userId }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId] });
      qc.invalidateQueries({ queryKey: ['groups'] });
    },
  });

  const removalMut = useMutation({
    mutationFn: async () => api.post(`/groups/${groupId}/removal-request`),
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['group', groupId] });
      qc.invalidateQueries({ queryKey: ['groups'] });
      if ((res.data as { completed?: boolean }).completed) {
        qc.removeQueries({ queryKey: ['group', groupId] });
        nav('/', { replace: true });
      }
    },
  });

  const timersMut = useMutation({
    mutationFn: async (body: Partial<GroupTimers>) =>
      (await api.patch(`/groups/${groupId}/timers`, body)).data as { timers: GroupTimers },
    onSuccess: (data) => {
      qc.setQueryData(['group', groupId, 'me'], (prev: GroupMe | undefined) =>
        prev
          ? { ...prev, group: { ...prev.group, timers: data.timers } }
          : prev
      );
      qc.invalidateQueries({ queryKey: ['groups'] });
    },
  });

  const cancelRemovalMut = useMutation({
    mutationFn: async () => api.post(`/groups/${groupId}/removal-request/cancel`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['group', groupId] });
    },
  });

  const deleteSoloMut = useMutation({
    mutationFn: async () => api.delete(`/groups/${groupId}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['groups'] });
      qc.removeQueries({ queryKey: ['group', groupId] });
      nav('/', { replace: true });
    },
  });

  if (!groupId) return null;

  if (meQ.isLoading) {
    return <LinearProgress />;
  }

  if (meQ.isError || !meQ.data?.group) {
    return <Alert severity="error">Could not load this group.</Alert>;
  }

  if (meQ.data.role !== 'creator') {
    return (
      <Alert severity="info">
        Only the group owner (creator) can change settings or delete the group. You are a member of{' '}
        <strong>{meQ.data.group.name}</strong>.
      </Alert>
    );
  }

  const dirty =
    name.trim() !== meQ.data.group.name ||
    locationKey.trim().toLowerCase() !== meQ.data.group.locationKey;

  const t0 = meQ.data.group.timers;
  const timersDirty =
    t0 == null ||
    junkRemovalGraceMinutes !== t0.junkRemovalGraceMinutes ||
    bidDuplicateLookbackDays !== t0.bidDuplicateLookbackDays ||
    possibleTimerMinutes !== (t0.possibleTimerMinutes ?? 0);

  const hasAssister = Boolean(meQ.data.removal?.assisterUserId);
  const r = meQ.data.removal;
  const ownerConfirmed = Boolean(r?.ownerConfirmedAt);
  const assisterConfirmed = Boolean(r?.assisterConfirmedAt);
  const removalPending = hasAssister && (ownerConfirmed || assisterConfirmed) && !(ownerConfirmed && assisterConfirmed);

  const assisterOptions =
    membersQ.data?.users.filter((u) => user && u._id !== user.id) ?? [];

  return (
    <Stack spacing={3} maxWidth={560}>
      <Box>
        <Typography variant="h5">Group settings</Typography>
        <Typography color="text.secondary" variant="body2">
          As the owner, you can rename the group, designate a removal assister for safer deletion, or remove the group.
          Deleting removes all bids, links, and interviews.
        </Typography>
      </Box>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" gutterBottom>
          Details
        </Typography>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField label="Group name" value={name} onChange={(e) => setName(e.target.value)} fullWidth />
          <TextField
            label="Location key (e.g. us, mexico)"
            value={locationKey}
            onChange={(e) => setLocationKey(e.target.value)}
            fullWidth
          />
          <Button
            variant="contained"
            disabled={!name.trim() || !locationKey.trim() || !dirty || patchMut.isPending}
            onClick={() => patchMut.mutate()}
          >
            Save changes
          </Button>
          {patchMut.isError && (
            <Alert severity="error">
              {(patchMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
                'Could not save.'}
            </Alert>
          )}
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" gutterBottom>
          Timers & detection
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Auto-removal waits this long after a link is marked useless (group owner can remove sooner). Duplicate
          URL / company+role flags only compare listings from the last N days. The last field is reserved for future
          timed features (0 = off).
        </Typography>
        <Stack spacing={2}>
          <TextField
            label="Junk auto-removal grace (minutes)"
            type="number"
            inputProps={{ min: 1, max: 10080 }}
            value={junkRemovalGraceMinutes}
            onChange={(e) => setJunkRemovalGraceMinutes(Number(e.target.value))}
            fullWidth
            size="small"
          />
          <TextField
            label="Bid duplicate detection lookback (days)"
            type="number"
            inputProps={{ min: 1, max: 3650 }}
            value={bidDuplicateLookbackDays}
            onChange={(e) => setBidDuplicateLookbackDays(Number(e.target.value))}
            fullWidth
            size="small"
          />
          <TextField
            label="Possible timer (minutes, reserved)"
            type="number"
            inputProps={{ min: 0, max: 10080 }}
            value={possibleTimerMinutes}
            onChange={(e) => setPossibleTimerMinutes(Number(e.target.value))}
            fullWidth
            size="small"
          />
          <Button
            variant="contained"
            disabled={!timersDirty || timersMut.isPending}
            onClick={() =>
              timersMut.mutate({
                junkRemovalGraceMinutes,
                bidDuplicateLookbackDays,
                possibleTimerMinutes,
              })
            }
          >
            Save timers
          </Button>
          {timersMut.isError && (
            <Alert severity="error">
              {(timersMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
                'Could not save timers.'}
            </Alert>
          )}
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle1" gutterBottom>
          Removal assister
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Choose another member who must confirm together with you before the group can be deleted. Leave empty to
          allow yourself to delete the group alone (still requires your confirmation in the danger zone).
        </Typography>
        <Autocomplete
          options={assisterOptions}
          getOptionLabel={(o) => o.nickname || o.email}
          value={assisterPick}
          onChange={(_, v) => setAssisterPick(v)}
          disabled={assisterMut.isPending || membersQ.isLoading}
          renderInput={(params) => <TextField {...params} label="Assister (group member)" placeholder="None" />}
        />
        <Stack direction="row" spacing={1} sx={{ mt: 2 }} flexWrap="wrap" useFlexGap>
          <Button
            variant="outlined"
            size="small"
            disabled={assisterMut.isPending}
            onClick={() => {
              const id = assisterPick?._id ?? null;
              const current = meQ.data.removal?.assisterUserId ?? null;
              if (id === current) return;
              assisterMut.mutate(id);
            }}
          >
            Save assister
          </Button>
          <Button
            variant="text"
            size="small"
            color="inherit"
            disabled={assisterMut.isPending || !meQ.data.removal?.assisterUserId}
            onClick={() => {
              setAssisterPick(null);
              assisterMut.mutate(null);
            }}
          >
            Clear assister
          </Button>
        </Stack>
        {assisterMut.isError && (
          <Alert severity="error" sx={{ mt: 2 }}>
            {(assisterMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
              'Could not update assister.'}
          </Alert>
        )}
      </Paper>

      <Paper variant="outlined" sx={{ p: 2, borderColor: 'error.dark' }}>
        <Typography variant="subtitle1" color="error" gutterBottom>
          Danger zone
        </Typography>

        {hasAssister ? (
          <>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
              With a removal assister, <strong>both of you</strong> must use the confirm action (here for you; the
              assister sees a banner in the sidebar). Deletion runs only after both confirmations.
            </Typography>
            {removalPending && (
              <Alert severity="warning" sx={{ mb: 2 }}>
                {ownerConfirmed && !assisterConfirmed && 'You confirmed. Waiting for the assister to confirm.'}
                {!ownerConfirmed && assisterConfirmed && 'The assister confirmed. Confirm below to finish deletion.'}
                {!ownerConfirmed && !assisterConfirmed && 'Neither party has confirmed yet.'}
              </Alert>
            )}
            <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
              <Button
                color="error"
                variant="contained"
                disabled={removalMut.isPending || (ownerConfirmed && !assisterConfirmed)}
                onClick={() => {
                  if (
                    !window.confirm(
                      `Confirm you agree to delete "${meQ.data.group.name}"? The assister must also confirm.`
                    )
                  ) {
                    return;
                  }
                  removalMut.mutate();
                }}
              >
                {ownerConfirmed && !assisterConfirmed ? 'Waiting for assister…' : 'I confirm deletion (owner)'}
              </Button>
              {removalPending && (
                <Button color="inherit" variant="outlined" disabled={cancelRemovalMut.isPending} onClick={() => cancelRemovalMut.mutate()}>
                  Cancel removal requests
                </Button>
              )}
            </Stack>
          </>
        ) : (
          <>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
              No removal assister is set. You can delete this group on your own.
            </Typography>
            <Button
              color="error"
              variant="outlined"
              disabled={deleteSoloMut.isPending}
              onClick={() => {
                if (
                  !window.confirm(
                    `Delete group "${meQ.data.group.name}" and all of its data? This cannot be undone.`
                  )
                ) {
                  return;
                }
                deleteSoloMut.mutate();
              }}
            >
              Delete group
            </Button>
          </>
        )}

        {removalMut.isError && (
          <Alert severity="error" sx={{ mt: 2 }}>
            {(removalMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
              'Could not process removal.'}
          </Alert>
        )}
        {deleteSoloMut.isError && (
          <Alert severity="error" sx={{ mt: 2 }}>
            {(deleteSoloMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
              'Could not delete.'}
          </Alert>
        )}
      </Paper>
    </Stack>
  );
}
