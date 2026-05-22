import { useQuery } from '@tanstack/react-query';
import api from '../api/client';

export type GroupMeResponse = {
  group: { _id: string; name: string };
  role: 'creator' | 'member' | 'none';
  isMember: boolean;
  effectiveRoles: Array<'admin' | 'bidder' | 'caller' | 'ops'>;
  memberRoles: Array<'bidder' | 'caller' | 'ops'>;
  watches: string[];
  status: 'pending' | 'approved';
  allowPastDayEdit: boolean;
};

export type GroupPermissions = {
  isAdmin: boolean;
  isBidder: boolean;
  isCaller: boolean;
  isOpsOnly: boolean;
  /** Convenience action flags — UI should use these instead of inspecting roles directly. */
  canBid: boolean;
  canCreateInterview: boolean;
  canEditInterview: boolean;
  canExport: boolean;
  canManageMembers: boolean;
  /** Group setting: when true, members can add/edit/delete bids on past-day boards too. */
  allowPastDayEdit: boolean;
  loading: boolean;
  effectiveRoles: GroupMeResponse['effectiveRoles'];
  watches: string[];
};

const EMPTY: GroupPermissions = {
  isAdmin: false,
  isBidder: false,
  isCaller: false,
  isOpsOnly: false,
  canBid: false,
  canCreateInterview: false,
  canEditInterview: false,
  canExport: false,
  canManageMembers: false,
  allowPastDayEdit: false,
  loading: false,
  effectiveRoles: [],
  watches: [],
};

export function useGroupPermissions(groupId: string | undefined): GroupPermissions {
  const q = useQuery({
    queryKey: ['group', groupId, 'me'] as const,
    enabled: !!groupId,
    queryFn: async () => (await api.get(`/groups/${groupId}/me`)).data as GroupMeResponse,
    staleTime: 60_000,
  });

  if (!groupId) return EMPTY;
  if (!q.data) return { ...EMPTY, loading: q.isLoading };

  const roles = q.data.effectiveRoles || [];
  const isAdmin = roles.includes('admin');
  const isBidder = roles.includes('bidder');
  const isCaller = roles.includes('caller');
  const isOps = roles.includes('ops');
  const isOpsOnly = !isAdmin && !isBidder && !isCaller && isOps;

  return {
    isAdmin,
    isBidder,
    isCaller,
    isOpsOnly,
    canBid: isAdmin || isBidder,
    canCreateInterview: isAdmin || isBidder,
    canEditInterview: isAdmin || isCaller,
    canExport: isAdmin || isBidder || isCaller,
    canManageMembers: isAdmin,
    allowPastDayEdit: Boolean(q.data.allowPastDayEdit),
    loading: false,
    effectiveRoles: roles,
    watches: q.data.watches || [],
  };
}
