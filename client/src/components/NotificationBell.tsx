import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Badge,
  Box,
  Button,
  Divider,
  IconButton,
  List,
  ListItem,
  ListItemText,
  Popover,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material';
import NotificationsNoneIcon from '@mui/icons-material/NotificationsNone';
import CheckIcon from '@mui/icons-material/Check';
import {
  getNotifications,
  markAllNotificationsRead,
  markNotificationRead,
  type NotificationItem,
} from '../api/profile';
import { getAppSocket } from '../socket/appSocket';

function describe(n: NotificationItem): string {
  if (n.kind === 'achievement') {
    const p = (n.payload || {}) as Record<string, unknown>;
    const kind = String(p.achievementKind || '');
    const target = p.target;
    const value = p.metricValue;
    if (kind === 'daily_bids') return `Daily goal hit: ${value}/${target} bids applied today.`;
    if (kind === 'weekly_interviews')
      return `Weekly goal hit: ${value}/${target} interviews this week.`;
    if (kind === 'monthly_offers') return `Monthly goal hit: ${value}/${target} offers this month.`;
  }
  return 'New notification.';
}

export function NotificationBell() {
  const qc = useQueryClient();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);

  const q = useQuery({
    queryKey: ['notifications', 'me'] as const,
    queryFn: () => getNotifications(false),
    refetchOnWindowFocus: true,
  });

  useEffect(() => {
    const socket = getAppSocket();
    const onNew = () => {
      void qc.invalidateQueries({ queryKey: ['notifications', 'me'] });
    };
    socket.on('notification:new', onNew);
    return () => {
      socket.off('notification:new', onNew);
    };
  }, [qc]);

  const readMut = useMutation({
    mutationFn: async (id: string) => markNotificationRead(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notifications', 'me'] }),
  });
  const readAllMut = useMutation({
    mutationFn: async () => markAllNotificationsRead(),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notifications', 'me'] }),
  });

  const items = q.data?.notifications ?? [];
  const unread = q.data?.unreadCount ?? 0;

  return (
    <>
      <Tooltip title="Notifications">
        <IconButton
          size="small"
          aria-label="Open notifications"
          onClick={(e) => setAnchor(e.currentTarget)}
        >
          <Badge color="error" badgeContent={unread} max={99} overlap="circular">
            <NotificationsNoneIcon />
          </Badge>
        </IconButton>
      </Tooltip>
      <Popover
        open={Boolean(anchor)}
        anchorEl={anchor}
        onClose={() => setAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
        slotProps={{ paper: { sx: { width: 360, maxHeight: 480 } } }}
      >
        <Stack
          direction="row"
          alignItems="center"
          justifyContent="space-between"
          sx={{ px: 1.5, py: 1, borderBottom: 1, borderColor: 'divider' }}
        >
          <Typography variant="subtitle2">Notifications</Typography>
          <Button
            size="small"
            disabled={unread === 0 || readAllMut.isPending}
            onClick={() => readAllMut.mutate()}
          >
            Mark all read
          </Button>
        </Stack>
        {items.length === 0 ? (
          <Box sx={{ p: 2 }}>
            <Typography variant="caption" color="text.secondary">
              No notifications yet.
            </Typography>
          </Box>
        ) : (
          <List dense disablePadding sx={{ maxHeight: 400, overflow: 'auto' }}>
            {items.map((n) => (
              <Box key={n.id}>
                <ListItem
                  alignItems="flex-start"
                  secondaryAction={
                    n.readAt ? null : (
                      <Tooltip title="Mark read">
                        <IconButton
                          size="small"
                          aria-label="Mark notification read"
                          disabled={readMut.isPending}
                          onClick={() => readMut.mutate(n.id)}
                        >
                          <CheckIcon fontSize="small" />
                        </IconButton>
                      </Tooltip>
                    )
                  }
                  sx={{
                    bgcolor: n.readAt ? 'transparent' : 'action.hover',
                    pr: n.readAt ? 1.5 : 6,
                  }}
                >
                  <ListItemText
                    primary={describe(n)}
                    primaryTypographyProps={{ variant: 'body2' }}
                    secondary={new Date(n.createdAt).toLocaleString()}
                    secondaryTypographyProps={{ variant: 'caption' }}
                  />
                </ListItem>
                <Divider component="li" />
              </Box>
            ))}
          </List>
        )}
      </Popover>
    </>
  );
}
