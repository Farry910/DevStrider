import type { QueryClient } from '@tanstack/react-query';
import type { BoardRow } from '../components/bid/BidBoardVirtualBody';

export type BidBoardData = {
  rows: BoardRow[];
  total: number;
  capped?: boolean;
};

/**
 * Bid-board page queries only — NOT the carryover-count query which also lives under the
 * `['bid-board', groupId, ...]` prefix. The page key is `['bid-board', groupId, day, sort, flag]`;
 * the count key is `['bid-board', groupId, 'carryover-count']`. We filter on key[2] to keep them
 * apart, otherwise the optimistic transform tries to patch `{ count }` shape and throws.
 */
function boardQueryKeys(qc: QueryClient, groupId: string) {
  return qc
    .getQueriesData<BidBoardData>({ queryKey: ['bid-board', groupId] })
    .map(([key]) => key)
    .filter((key) => {
      const third = key[2];
      return typeof third === 'string' && third !== 'carryover-count';
    });
}

/**
 * Read-modify-write every cached bid-board page query for this group. Returns previous snapshots
 * for rollback. Skips any cache entry that doesn't look like bid-board data (defence-in-depth so
 * a future key collision can't take down all mutations).
 */
export function patchAllBoardQueries(
  qc: QueryClient,
  groupId: string,
  transform: (data: BidBoardData) => BidBoardData
): Array<[readonly unknown[], BidBoardData | undefined]> {
  const snapshots: Array<[readonly unknown[], BidBoardData | undefined]> = [];
  for (const key of boardQueryKeys(qc, groupId)) {
    const prev = qc.getQueryData<BidBoardData>(key);
    snapshots.push([key, prev]);
    if (prev && Array.isArray(prev.rows) && typeof prev.total === 'number') {
      qc.setQueryData<BidBoardData>(key, transform(prev));
    }
  }
  return snapshots;
}

export function rollbackBoardQueries(
  qc: QueryClient,
  snapshots: Array<[readonly unknown[], BidBoardData | undefined]>
) {
  for (const [key, prev] of snapshots) {
    qc.setQueryData(key, prev);
  }
}

/** True for placeholder rows added optimistically before the server confirms. */
export function isOptimisticId(id: string | null | undefined): boolean {
  return typeof id === 'string' && id.startsWith('optimistic-');
}

/** Build an empty bid-board row for a newly typed URL, pending the server response. */
export function makeOptimisticLinkRow(args: {
  url: string;
  userId: string;
  nickname: string;
  avatarId: string;
}): BoardRow {
  const ts = Date.now();
  const tempId = `optimistic-${ts}-${Math.random().toString(36).slice(2, 8)}`;
  const now = new Date().toISOString();
  return {
    link: {
      id: tempId,
      url: args.url,
      sharedJobDescription: '',
      createdAt: now,
      createdByUserId: args.userId,
      markedUselessAt: null,
      appliedAt: null,
      junkPurgeEligible: false,
      createdBy: {
        nickname: args.nickname,
        avatarId: args.avatarId,
        avatarBadge: null,
      },
    },
    linkDuplicate: false,
    duplicateEarlierUrlBid: null,
    duplicateCompanyRole: false,
    duplicateEarlierBid: null,
    companyInterviewWarning: false,
    groupBidsOnLink: [],
    myBid: {
      id: `${tempId}-bid`,
      resumeId: '',
      company: '',
      role: '',
      primaryStacks: [],
      status: 'draft',
      origin: 'LinkedIn',
      jobDescription: '',
      gptResumeContent: '',
      comment: '',
      firstCreatedAt: now,
      updatedAt: now,
      lastModifiedBy: null,
    },
  };
}
