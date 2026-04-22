import { useEffect } from 'react';
import type { QueryClient } from '@tanstack/react-query';
import { getAppSocket } from '../socket/appSocket';

/**
 * Subscribes to `bidboard:invalidate` for a group so teammates’ REST changes refresh the bid board
 * without polling.
 */
export function useBidBoardSocketInvalidation(
  groupId: string | undefined,
  enabled: boolean,
  qc: QueryClient
) {
  useEffect(() => {
    if (!groupId || !enabled) return;
    const socket = getAppSocket();
    const gid = groupId;

    const join = () => {
      socket.emit('bidboard:join', gid, (r: { ok?: boolean; error?: string }) => {
        if (r?.error) {
          console.warn('bidboard:join', r.error);
        }
      });
    };

    const onInvalidate = (payload: { groupId?: string }) => {
      if (payload?.groupId !== gid) return;
      void qc.invalidateQueries({ queryKey: ['bid-board', gid] });
    };

    socket.on('connect', join);
    socket.on('bidboard:invalidate', onInvalidate);
    if (socket.connected) join();

    return () => {
      socket.emit('bidboard:leave', gid);
      socket.off('connect', join);
      socket.off('bidboard:invalidate', onInvalidate);
    };
  }, [groupId, enabled, qc]);
}
