import {
  Avatar,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Typography,
} from '@mui/material';
import { useMutation } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import api from '../api/client';
import { AVATAR_PRESET_IDS, presetAvatarSrc, type AvatarId } from '../avatarPresets';

type SessionUser = { id: string; email: string; nickname: string; avatarId: string };

function initialLetter(nickname: string) {
  const t = nickname.trim();
  if (!t) return '?';
  return t[0]!.toUpperCase();
}

type Props = {
  open: boolean;
  onClose: () => void;
  nickname: string;
  avatarId: string;
  onSaved: (user: SessionUser) => void;
};

export function ProfileAvatarDialog({ open, onClose, nickname, avatarId, onSaved }: Props) {
  const [selected, setSelected] = useState<AvatarId>('initial');

  useEffect(() => {
    if (open) {
      setSelected(
        AVATAR_PRESET_IDS.includes(avatarId as AvatarId) ? (avatarId as AvatarId) : 'initial'
      );
    }
  }, [open, avatarId]);

  const save = useMutation({
    mutationFn: async (id: AvatarId) => {
      const { data } = await api.patch<{ user: SessionUser }>('/auth/me', { avatarId: id });
      return data.user;
    },
    onSuccess: (user) => {
      onSaved(user);
      onClose();
    },
  });

  return (
    <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth aria-labelledby="avatar-dialog-title">
      <DialogTitle id="avatar-dialog-title">Profile picture</DialogTitle>
      <DialogContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Shown next to job links you add on the bid board. Choose a preset or your initial.
        </Typography>
        <Box
          sx={{
            display: 'grid',
            gridTemplateColumns: 'repeat(4, 1fr)',
            gap: 1.5,
            justifyItems: 'center',
          }}
        >
          {AVATAR_PRESET_IDS.map((id) => {
            const src = presetAvatarSrc(id);
            const active = selected === id;
            return (
              <Box key={id} sx={{ textAlign: 'center' }}>
                <Avatar
                  onClick={() => setSelected(id)}
                  src={src ?? undefined}
                  sx={{
                    width: 56,
                    height: 56,
                    cursor: 'pointer',
                    fontSize: '1.25rem',
                    bgcolor: 'primary.dark',
                    boxShadow: active ? 4 : 0,
                    outline: active ? 2 : 0,
                    outlineColor: 'primary.main',
                    outlineOffset: 2,
                  }}
                >
                  {id === 'initial' ? initialLetter(nickname) : null}
                </Avatar>
                <Typography variant="caption" color="text.secondary" display="block" sx={{ mt: 0.5 }}>
                  {id === 'initial' ? 'Initial' : `Preset ${id.slice(-1)}`}
                </Typography>
              </Box>
            );
          })}
        </Box>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button onClick={onClose} color="inherit">
          Cancel
        </Button>
        <Button
          variant="contained"
          disabled={save.isPending}
          onClick={() => save.mutate(selected)}
        >
          Save
        </Button>
      </DialogActions>
    </Dialog>
  );
}
