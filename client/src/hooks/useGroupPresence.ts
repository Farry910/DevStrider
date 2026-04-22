import { useEffect, useState } from 'react';
import { getAppSocket } from '../socket/appSocket';

export type PresenceUser = { id: string; nickname: string; avatarId: string };

/**
 * Subscribes to group presence on the shared app socket.
 * Re-joins the presence room on every reconnect.
 */
export function useGroupPresence(groupId: string | undefined, enabled: boolean) {
  const [users, setUsers] = useState<PresenceUser[]>([]);

  useEffect(() => {
    if (!groupId || !enabled) {
      setUsers([]);
      return;
    }

    const token = localStorage.getItem('devstrider_token');
    if (!token) {
      setUsers([]);
      return;
    }

    const socket = getAppSocket();

    const onUpdate = (payload: { users?: PresenceUser[] }) => {
      setUsers(Array.isArray(payload?.users) ? payload.users : []);
    };

    const joinPresence = () => {
      socket.emit('presence:join', groupId, (res: { ok?: boolean; error?: string }) => {
        if (res?.error) setUsers([]);
      });
    };

    socket.on('presence:update', onUpdate);
    socket.on('connect', joinPresence);
    if (socket.connected) joinPresence();

    return () => {
      socket.emit('presence:leave', groupId);
      socket.off('presence:update', onUpdate);
      socket.off('connect', joinPresence);
    };
  }, [groupId, enabled]);

  return users;
}
