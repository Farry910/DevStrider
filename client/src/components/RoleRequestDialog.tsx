import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import api from '../api/client';

type Role = 'bidder' | 'caller' | 'ops';

type Props = {
  open: boolean;
  onClose: () => void;
  groupId: string;
  /** Current roles, used to pre-tick the checkboxes. */
  currentRoles?: Role[];
};

const ALL_ROLES: Array<{ key: Role; label: string; hint: string }> = [
  { key: 'bidder', label: 'BIDDER', hint: 'Add links and bid on jobs' },
  { key: 'caller', label: 'CALLER', hint: 'Edit interviews for assigned bidders' },
  { key: 'ops', label: 'OPS', hint: 'Read-only on assigned users' },
];

/**
 * Submits a role-change request to the group owner. Notification fires on the owner's bell;
 * actual role change is applied manually by the owner from the Members & roles panel.
 */
export function RoleRequestDialog({ open, onClose, groupId, currentRoles = [] }: Props) {
  const [roles, setRoles] = useState<Role[]>(currentRoles);
  const [message, setMessage] = useState('');
  const [done, setDone] = useState(false);

  const mut = useMutation({
    mutationFn: async () =>
      api.post(`/groups/${groupId}/role-requests`, {
        requestedRoles: roles,
        message: message.trim() || undefined,
      }),
    onSuccess: () => {
      setDone(true);
    },
  });

  function toggle(r: Role) {
    setRoles((prev) => (prev.includes(r) ? prev.filter((x) => x !== r) : [...prev, r]));
  }

  function handleClose() {
    setDone(false);
    setMessage('');
    setRoles(currentRoles);
    onClose();
  }

  return (
    <Dialog open={open} onClose={handleClose} maxWidth="xs" fullWidth>
      <DialogTitle>Request role change</DialogTitle>
      <DialogContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          The group owner will get a notification with your request. They'll grant the roles
          manually if approved.
        </Typography>
        <Stack spacing={0.5}>
          {ALL_ROLES.map((r) => (
            <FormControlLabel
              key={r.key}
              control={
                <Checkbox
                  size="small"
                  checked={roles.includes(r.key)}
                  onChange={() => toggle(r.key)}
                />
              }
              label={
                <span>
                  <strong>{r.label}</strong>{' '}
                  <Typography variant="caption" color="text.secondary" component="span">
                    — {r.hint}
                  </Typography>
                </span>
              }
            />
          ))}
        </Stack>
        <TextField
          fullWidth
          size="small"
          label="Note for the owner (optional)"
          value={message}
          onChange={(e) => setMessage(e.target.value)}
          multiline
          rows={2}
          inputProps={{ maxLength: 500 }}
          sx={{ mt: 2 }}
        />
        {mut.isError && (
          <Alert severity="error" sx={{ mt: 2 }}>
            {(mut.error as { response?: { data?: { error?: string } } })?.response?.data?.error ??
              'Could not send the request.'}
          </Alert>
        )}
        {done && (
          <Alert severity="success" sx={{ mt: 2 }}>
            Request sent. You'll see the result in your roles when the owner acts on it.
          </Alert>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={handleClose}>{done ? 'Close' : 'Cancel'}</Button>
        {!done && (
          <Button
            variant="contained"
            disabled={roles.length === 0 || mut.isPending}
            onClick={() => mut.mutate()}
          >
            {mut.isPending ? 'Sending…' : 'Send request'}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
