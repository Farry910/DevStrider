import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Checkbox,
  Chip,
  IconButton,
  LinearProgress,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import EditIcon from '@mui/icons-material/Edit';
import CloseIcon from '@mui/icons-material/Close';
import CheckIcon from '@mui/icons-material/Check';
import api from '../api/client';

type Role = 'bidder' | 'caller' | 'ops';
type MemberRow = {
  userId: string;
  nickname: string;
  email: string;
  avatarId: string;
  roles: Role[];
  watches: string[];
  joinedAt: string;
  isCreator: boolean;
};

type Props = {
  groupId: string;
  isAdmin: boolean;
};

const ALL_ROLES: Role[] = ['bidder', 'caller', 'ops'];

export function GroupMembersPanel({ groupId, isAdmin }: Props) {
  const qc = useQueryClient();
  const [editingFor, setEditingFor] = useState<string | null>(null);
  const [draftRoles, setDraftRoles] = useState<Role[]>([]);
  const [draftWatches, setDraftWatches] = useState<string[]>([]);

  const membersQ = useQuery({
    queryKey: ['group', groupId, 'members-detailed'] as const,
    enabled: !!groupId,
    queryFn: async () =>
      (await api.get(`/groups/${groupId}/members-detailed`)).data as { members: MemberRow[] },
  });

  const members = membersQ.data?.members ?? [];

  const byId = useMemo(() => {
    const map = new Map<string, MemberRow>();
    for (const m of members) map.set(m.userId, m);
    return map;
  }, [members]);

  const rolesMut = useMutation({
    mutationFn: async (vars: { userId: string; roles: Role[] }) =>
      api.patch(`/groups/${groupId}/members/${vars.userId}/roles`, { roles: vars.roles }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['group', groupId, 'members-detailed'] }),
  });

  const watchesMut = useMutation({
    mutationFn: async (vars: { userId: string; watches: string[] }) =>
      api.patch(`/groups/${groupId}/members/${vars.userId}/watches`, { watches: vars.watches }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['group', groupId, 'members-detailed'] }),
  });

  function startEdit(m: MemberRow) {
    setEditingFor(m.userId);
    setDraftRoles([...m.roles]);
    setDraftWatches([...m.watches]);
  }
  function cancelEdit() {
    setEditingFor(null);
  }
  async function saveEdit(m: MemberRow) {
    /** Persist roles and watches separately; both PATCH endpoints are idempotent. */
    await rolesMut.mutateAsync({ userId: m.userId, roles: draftRoles });
    await watchesMut.mutateAsync({ userId: m.userId, watches: draftWatches });
    setEditingFor(null);
  }

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Typography variant="subtitle1" gutterBottom>
        Members &amp; roles
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Default for new joiners is OPS (read-only on watched users). Grant BIDDER to let them bid;
        grant CALLER to let them edit interviews from their watched bidders. The group owner is
        ADMIN (implicit) and isn't listed for role edits — use the platform admin to transfer.
      </Typography>
      {membersQ.isLoading && <LinearProgress />}
      {membersQ.isError && <Alert severity="error">Could not load members.</Alert>}
      {(rolesMut.isError || watchesMut.isError) && (
        <Alert severity="error" sx={{ mb: 1 }}>
          {(rolesMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
            (watchesMut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
            'Failed to save changes.'}
        </Alert>
      )}
      <Stack spacing={1.5}>
        {members.map((m) => {
          const editing = editingFor === m.userId;
          const watchableOptions = members.filter((mm) => mm.userId !== m.userId);
          return (
            <Box
              key={m.userId}
              sx={{
                p: 1.25,
                border: 1,
                borderColor: editing ? 'primary.main' : 'divider',
                borderRadius: 1,
              }}
            >
              <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
                <Box sx={{ minWidth: 0 }}>
                  <Typography variant="body2" fontWeight={600} noWrap>
                    {m.nickname || m.email}
                    {m.isCreator && (
                      <Chip
                        size="small"
                        color="primary"
                        label="ADMIN (owner)"
                        sx={{ ml: 1, height: 18, '& .MuiChip-label': { px: 0.85, fontSize: '0.65rem' } }}
                      />
                    )}
                  </Typography>
                  <Typography variant="caption" color="text.secondary" noWrap>
                    {m.email}
                  </Typography>
                </Box>
                {isAdmin && !m.isCreator && (
                  <Box>
                    {!editing ? (
                      <IconButton size="small" aria-label="Edit roles" onClick={() => startEdit(m)}>
                        <EditIcon fontSize="small" />
                      </IconButton>
                    ) : (
                      <>
                        <IconButton
                          size="small"
                          color="primary"
                          aria-label="Save"
                          disabled={rolesMut.isPending || watchesMut.isPending}
                          onClick={() => void saveEdit(m)}
                        >
                          <CheckIcon fontSize="small" />
                        </IconButton>
                        <IconButton
                          size="small"
                          aria-label="Cancel"
                          disabled={rolesMut.isPending || watchesMut.isPending}
                          onClick={cancelEdit}
                        >
                          <CloseIcon fontSize="small" />
                        </IconButton>
                      </>
                    )}
                  </Box>
                )}
              </Stack>
              <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" useFlexGap>
                <Typography variant="caption" color="text.secondary" sx={{ mr: 0.5 }}>
                  Roles:
                </Typography>
                {!editing
                  ? (m.isCreator ? (['admin'] as const) : m.roles).map((r) => (
                      <Chip
                        key={r}
                        size="small"
                        label={r.toUpperCase()}
                        color={r === 'admin' ? 'primary' : r === 'bidder' ? 'success' : r === 'caller' ? 'info' : 'default'}
                        variant="outlined"
                        sx={{ height: 20, '& .MuiChip-label': { px: 0.85, fontSize: '0.7rem' } }}
                      />
                    ))
                  : ALL_ROLES.map((r) => (
                      <Chip
                        key={r}
                        size="small"
                        label={r.toUpperCase()}
                        color={draftRoles.includes(r) ? 'primary' : 'default'}
                        variant={draftRoles.includes(r) ? 'filled' : 'outlined'}
                        onClick={() =>
                          setDraftRoles((prev) =>
                            prev.includes(r) ? prev.filter((x) => x !== r) : [...prev, r]
                          )
                        }
                        sx={{ height: 22, '& .MuiChip-label': { px: 0.85, fontSize: '0.7rem' } }}
                      />
                    ))}
              </Stack>
              {(m.roles.includes('caller') ||
                m.roles.includes('ops') ||
                (editing && (draftRoles.includes('caller') || draftRoles.includes('ops')))) && (
                <Box sx={{ mt: 1 }}>
                  <Typography variant="caption" color="text.secondary">
                    Watches:
                  </Typography>
                  {!editing ? (
                    <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap sx={{ mt: 0.5 }}>
                      {m.watches.length === 0 ? (
                        <Typography variant="caption" color="warning.main">
                          (no users watched yet — assign at least one to give visibility)
                        </Typography>
                      ) : (
                        m.watches.map((wid) => {
                          const u = byId.get(wid);
                          return (
                            <Chip
                              key={wid}
                              size="small"
                              variant="outlined"
                              label={u?.nickname || wid}
                              sx={{ height: 20, '& .MuiChip-label': { px: 0.85, fontSize: '0.7rem' } }}
                            />
                          );
                        })
                      )}
                    </Stack>
                  ) : (
                    <Autocomplete
                      multiple
                      size="small"
                      options={watchableOptions}
                      getOptionLabel={(o) => o.nickname || o.email}
                      isOptionEqualToValue={(a, b) => a.userId === b.userId}
                      value={watchableOptions.filter((o) => draftWatches.includes(o.userId))}
                      onChange={(_, v) => setDraftWatches(v.map((x) => x.userId))}
                      renderInput={(params) => (
                        <TextField {...params} placeholder="Pick users this member can see" />
                      )}
                      renderOption={(props, option, { selected }) => (
                        <li {...props} key={option.userId}>
                          <Checkbox
                            size="small"
                            checked={selected}
                            sx={{ p: 0.5, mr: 0.5 }}
                          />
                          <Box sx={{ minWidth: 0 }}>
                            <Typography variant="body2">{option.nickname || option.email}</Typography>
                            <Typography variant="caption" color="text.secondary">
                              {option.email}
                            </Typography>
                          </Box>
                        </li>
                      )}
                      sx={{ mt: 0.5 }}
                    />
                  )}
                </Box>
              )}
            </Box>
          );
        })}
      </Stack>
      {!isAdmin && (
        <Stack direction="row" spacing={1} sx={{ mt: 1.5 }}>
          <Button size="small" variant="text" disabled>
            Owner-only — sign in as group admin to edit roles
          </Button>
        </Stack>
      )}
    </Paper>
  );
}
