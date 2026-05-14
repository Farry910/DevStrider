import { useEffect, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import {
  Avatar,
  Box,
  Button,
  Chip,
  Collapse,
  IconButton,
  ListItemIcon,
  ListItemText,
  Menu,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import CheckIcon from '@mui/icons-material/Check';
import EditIcon from '@mui/icons-material/Edit';
import EventIcon from '@mui/icons-material/Event';
import WarningAmberIcon from '@mui/icons-material/WarningAmber';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import MoreVertIcon from '@mui/icons-material/MoreVert';
import CloseIcon from '@mui/icons-material/Close';
import type { Dispatch, SetStateAction } from 'react';
import {
  bidBoardCellSx,
  bidBoardFastFeedFieldSx,
  bidBoardRowGridSx,
  bidBoardStickyActionsSx,
  bidBoardTextFieldSx,
  bidBoardTextFieldSingleLineEllipsisSx,
} from './bidBoardGrid';
import { toHtmlTimeInputValue } from '../../utils/timeInput';
import { BID_BOARD_TOOLTIP_COMMON } from '../../utils/bidBoardTooltip';
import { LinkCreatorBadge, type LinkCreatorAvatarBadge } from '../LinkCreatorBadge';

/** Match link-creator avatar styling for bidder stack (badge tint + ring). */
function bidderAvatarSx(
  theme: Theme,
  avatarId: string,
  badge: LinkCreatorAvatarBadge | null | undefined,
  size: number
) {
  const src = presetAvatarSrc(avatarId);
  const hasImage = Boolean(src);
  const tintColor = badge?.color;
  const base = {
    width: size,
    height: size,
    fontSize: Math.round(size * 0.45),
  };
  if (tintColor && hasImage) {
    return {
      ...base,
      bgcolor: 'primary.dark',
      color: 'primary.contrastText',
      border: '2px solid',
      borderColor: alpha(tintColor, 0.9),
      boxShadow: `0 0 0 2px ${tintColor}`,
    };
  }
  if (tintColor && !hasImage) {
    return {
      ...base,
      bgcolor: tintColor,
      color: '#fff',
      border: '2px solid',
      borderColor: alpha(tintColor, 0.85),
      boxShadow: `inset 0 0 0 1px ${alpha('#000', 0.15)}`,
    };
  }
  return {
    ...base,
    bgcolor: 'primary.dark',
    color: 'primary.contrastText',
    border: '2px solid',
    borderColor: alpha(theme.palette.primary.light, 0.55),
    boxShadow: `inset 0 0 0 1px ${alpha(theme.palette.common.black, 0.2)}, 0 0 0 1px ${alpha(theme.palette.divider, 0.9)}`,
  };
}
import { FormatStatusBadge } from '../FormatStatusBadge';
import { alpha, useTheme, type Theme } from '@mui/material/styles';
import { presetAvatarSrc } from '../../avatarPresets';
import { composeResume } from '../../utils/composeResume';
import type { Profile } from '../../api/profile';

const BID_STATUSES = [
  'draft',
  'applied',
  'screening',
  'interview',
  'offer',
  'rejected',
  'withdrawn',
  'accepted',
] as const;

export type EarlierDuplicateSummary = {
  url: string;
  resumeId: string;
  status: string;
  createdAt: string;
  /** True when the match uses a teammate’s company/role on a shared link — no detail rows are shown. */
  hiddenReference?: boolean;
};

export type GroupBidOnLink = {
  userId: string;
  nickname: string;
  avatarId: string;
  status: string;
  filled: boolean;
  avatarBadge?: LinkCreatorAvatarBadge | null;
};

export type BoardRow = {
  link: {
    id: string;
    url: string;
    sharedJobDescription: string;
    createdAt: string;
    updatedAt?: string;
    createdByUserId: string;
    /** Link creator marked posting as useless; owner may purge if `junkPurgeEligible` */
    markedUselessAt: string | null;
    appliedAt: string | null;
    junkPurgeEligible: boolean;
    createdBy: { nickname: string; avatarId: string; avatarBadge?: LinkCreatorAvatarBadge | null };
  };
  linkDuplicate: boolean;
  /** Per-user: set only when this viewer already has an earlier bid on the same URL (another listing). */
  duplicateEarlierUrlBid: EarlierDuplicateSummary | null;
  duplicateCompanyRole: boolean;
  duplicateEarlierBid: EarlierDuplicateSummary | null;
  companyInterviewWarning: boolean;
  /** Members with status `applied` on this link (avatars). */
  groupBidsOnLink: GroupBidOnLink[];
  myBid: {
    id: string;
    resumeId: string;
    company: string;
    role: string;
    primaryStacks: string[];
    status: string;
    origin: string;
    jobDescription: string;
    gptResumeContent: string;
    comment: string;
    /** Immutable first creation time for this bid row (server: firstCreatedAt). */
    firstCreatedAt?: string;
    updatedAt: string;
    lastModifiedBy: { nickname: string } | null;
  } | null;
};

type MutPatchBid = (payload: { bidId: string; body: Record<string, unknown> }) => void;
type MutDeleteBid = (bidId: string) => void;

/** Visible URL with the scheme stripped — full URL still drives href and tooltip. */
function displayUrl(url: string) {
  return url.replace(/^https?:\/\//i, '');
}

function mergedJdText(row: BoardRow, b: BoardRow['myBid']) {
  if (!b) return (row.link.sharedJobDescription || '').trim();
  const own = (b.jobDescription || '').trim();
  if (own) return own;
  return (row.link.sharedJobDescription || '').trim();
}

function jdAttachedLabel(row: BoardRow, b: NonNullable<BoardRow['myBid']>) {
  return mergedJdText(row, b).trim() ? 'attached' : 'none';
}

function commentAttachedLabel(b: NonNullable<BoardRow['myBid']>) {
  return (b.comment || '').trim() ? 'commented' : 'none';
}

function gptResumeAttachedLabel(b: NonNullable<BoardRow['myBid']>) {
  return (b.gptResumeContent || '').trim() ? 'attached' : 'none';
}

type Props = {
  /** Scroll container; use state in parent so this updates when the node mounts (ref alone won’t re-render). */
  scrollElement: HTMLDivElement | null;
  rows: BoardRow[];
  expandedBidId: string | null;
  setExpandedBid: (id: string | null) => void;
  fastFeed: Record<string, string>;
  setFastFeed: Dispatch<SetStateAction<Record<string, string>>>;
  commitFastFeed: (linkId: string, existingBidId: string | null) => void | Promise<void>;
  patchBid: { mutate: MutPatchBid };
  readOnly: boolean;
  deleteBid: { mutate: MutDeleteBid; isPending: boolean };
  ivDraft: {
    meetingLink: string;
    scheduledDate: string;
    scheduledTime: string;
    recruiter: string;
  };
  setIvDraft: Dispatch<
    SetStateAction<{
      meetingLink: string;
      scheduledDate: string;
      scheduledTime: string;
      recruiter: string;
    }>
  >;
  createInterview: { mutate: (bidId: string) => void; isPending: boolean };
  /** Highlights your avatar in the group bidders stack. */
  currentUserId?: string | null;
  /** Today only: fast-feed / new bid flow. Editing existing bids does not depend on this. */
  allowNewInputFlow: boolean;
  /** Link creator marks useless / clears flag (REST). */
  patchLinkUseless: {
    mutate: (vars: { linkId: string; useless: boolean }) => void;
    isPending: boolean;
  };
  /** Caller's profile — used to compose the owner-only resume hover with header + body. */
  myProfile?: Profile | null;
};

function EarlierBidTooltipBody({
  heading,
  detail,
}: {
  heading: string;
  detail: EarlierDuplicateSummary;
}) {
  return (
    <Box sx={{ maxWidth: '100%' }}>
      <Typography variant="caption" fontWeight={600} display="block" sx={{ mb: 0.5 }}>
        {heading}
      </Typography>
      <Typography variant="caption" component="div" sx={{ wordBreak: 'break-all', opacity: 0.95 }}>
        {detail.url}
      </Typography>
      <Typography variant="caption" color="inherit" display="block" sx={{ mt: 0.75, opacity: 0.85 }}>
        Resume {detail.resumeId?.trim() || '—'} · {detail.status}
      </Typography>
      <Typography variant="caption" display="block" sx={{ mt: 0.25, opacity: 0.75 }}>
        Added {new Date(detail.createdAt).toLocaleString()}
      </Typography>
    </Box>
  );
}

function CompanyRoleDupTooltip({ detail }: { detail: EarlierDuplicateSummary }) {
  if (detail.hiddenReference) {
    return (
      <Typography variant="caption" component="div" sx={{ maxWidth: '100%' }}>
        The same company and role already apply to another of your bids, or another member applied first on a
        shared link. Their application is used only for duplicate detection — details are not shown.
      </Typography>
    );
  }
  return (
    <EarlierBidTooltipBody
      heading="Earlier job link in this group (same company and role)"
      detail={detail}
    />
  );
}

function GroupBidderStack({
  bids,
  currentUserId,
  dense,
}: {
  bids: GroupBidOnLink[];
  currentUserId?: string | null;
  dense?: boolean;
}) {
  const theme = useTheme();
  if (!bids.length) {
    return (
      <Typography variant="caption" color="text.secondary" component="span">
        —
      </Typography>
    );
  }
  const size = dense ? 22 : 26;
  const overlap = dense ? -0.65 : -0.75;
  return (
    <Stack
      direction="row"
      alignItems="center"
      justifyContent="center"
      sx={{ minWidth: 0, width: '100%', py: 0.25 }}
    >
      <Box
        component="span"
        sx={{
          display: 'inline-flex',
          flexDirection: 'row',
          alignItems: 'center',
          pl: 0.25,
        }}
      >
        {bids.map((m, i) => {
          const label = m.nickname?.trim() || 'Member';
          const draftNote = !m.filled && m.status === 'draft' ? ' · draft row' : '';
          const badgeHint =
            m.avatarBadge?.titles?.length && m.avatarBadge.titles.length > 0
              ? ` · ${m.avatarBadge.titles.join(', ')}`
              : '';
          return (
            <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
              key={m.userId}
              title={`${label} · ${m.status || '—'}${draftNote}${badgeHint}`}
              placement="top"
            >
              <Avatar
                src={presetAvatarSrc(m.avatarId) ?? undefined}
                sx={{
                  ...bidderAvatarSx(theme, m.avatarId, m.avatarBadge, size),
                  ml: i > 0 ? overlap : 0,
                  boxSizing: 'content-box',
                  opacity: m.filled ? 1 : 0.55,
                  ...(currentUserId && m.userId === currentUserId
                    ? {
                        borderColor: 'primary.main',
                        boxShadow: `0 0 0 2px ${theme.palette.primary.main}`,
                      }
                    : !(m.avatarBadge?.color)
                      ? {
                          borderColor: 'background.paper',
                        }
                      : {}),
                }}
              >
                {label.charAt(0).toUpperCase() || '?'}
              </Avatar>
            </Tooltip>
          );
        })}
      </Box>
    </Stack>
  );
}

async function copyToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    /* ignore */
  }
}

function CellCopyButton({
  getText,
  label,
  disableWhenEmpty = true,
}: {
  getText: () => string;
  label: string;
  /** When false, the button stays enabled so copy still works for uncontrolled fields (value read at click). */
  disableWhenEmpty?: boolean;
}) {
  const empty = !getText().trim();
  return (
    <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title={`Copy ${label}`}>
      <span>
        <IconButton
          size="small"
          aria-label={`Copy ${label}`}
          disabled={disableWhenEmpty && empty}
          onClick={() => void copyToClipboard(getText())}
          sx={{ flexShrink: 0, p: 0.25, ml: 0.25 }}
        >
          <ContentCopyIcon sx={{ fontSize: 16 }} />
        </IconButton>
      </span>
    </Tooltip>
  );
}

function RoText({ children }: { children: string }) {
  return (
    <Typography
      variant="body2"
      noWrap
      component="div"
      title={children || undefined}
      sx={{
        width: '100%',
        maxWidth: '100%',
        textAlign: 'center',
        overflow: 'hidden',
        textOverflow: 'ellipsis',
      }}
    >
      {children || '—'}
    </Typography>
  );
}

/** Filled “Useless” chip visible to all members; creator can clear via delete on chip when allowed. */
function UselessMarkChip({
  row,
  currentUserId,
  readOnly,
  patchLinkUseless,
}: {
  row: BoardRow;
  currentUserId?: string | null;
  readOnly: boolean;
  patchLinkUseless: {
    mutate: (vars: { linkId: string; useless: boolean }) => void;
    isPending: boolean;
  };
}) {
  if (!row.link.markedUselessAt) return null;
  const isCreator = Boolean(currentUserId && row.link.createdByUserId === currentUserId);
  return (
    <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
      title={
        isCreator
          ? 'Marked useless (visible to the group) — remove to clear'
          : 'Link creator marked this posting as useless'
      }
    >
      <Chip
        label="Useless"
        size="small"
        color="error"
        variant="filled"
        {...(isCreator && !readOnly
          ? {
              onDelete: patchLinkUseless.isPending
                ? undefined
                : () =>
                    patchLinkUseless.mutate({
                      linkId: row.link.id,
                      useless: false,
                    }),
            }
          : {})}
        sx={{
          height: 24,
          maxHeight: 24,
          '& .MuiChip-label': { px: 0.85, fontSize: '0.7rem' },
        }}
      />
    </Tooltip>
  );
}

export function BidBoardVirtualBody({
  scrollElement,
  rows,
  expandedBidId,
  setExpandedBid,
  fastFeed,
  setFastFeed,
  commitFastFeed,
  patchBid,
  readOnly,
  deleteBid,
  ivDraft,
  setIvDraft,
  createInterview,
  currentUserId,
  allowNewInputFlow,
  patchLinkUseless,
  myProfile,
}: Props) {
  const [editingBidId, setEditingBidId] = useState<string | null>(null);
  const [bidActionsMenu, setBidActionsMenu] = useState<{
    bidId: string;
    anchor: HTMLElement;
  } | null>(null);
  const jdInputRefs = useRef(new Map<string, HTMLInputElement | HTMLTextAreaElement>());
  const gptResumeInputRefs = useRef(new Map<string, HTMLInputElement | HTMLTextAreaElement>());
  const commentInputRefs = useRef(new Map<string, HTMLInputElement | HTMLTextAreaElement>());

  const closeBidActionsMenu = () => setBidActionsMenu(null);

  useEffect(() => {
    if (readOnly) {
      setEditingBidId(null);
    }
  }, [readOnly]);

  const finishEditing = () => {
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    setEditingBidId(null);
  };

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollElement,
    estimateSize: () => 44,
    overscan: 12,
    getItemKey: (index) => rows[index]?.link?.id ?? String(index),
  });

  const vitems = virtualizer.getVirtualItems();

  return (
    <>
    <Box
      sx={{
        height: virtualizer.getTotalSize(),
        position: 'relative',
        width: '100%',
      }}
    >
      {vitems.map((v) => {
        const row = rows[v.index];
        if (!row) return null;
        const b = row.myBid;
        const rowEditing = Boolean(b && !readOnly && editingBidId === b.id);
        /** Server creates an empty UserBid when you add a link; treat like bid-ready until resume/company/role are set. */
        const isUnfilledBid = b
          ? !String(b.resumeId || '').trim() &&
            !String(b.company || '').trim() &&
            !String(b.role || '').trim()
          : false;
        const bidReadyCompactLayout = !b || (isUnfilledBid && !rowEditing);
        const showFastFeed =
          allowNewInputFlow && !readOnly && (!b || rowEditing || isUnfilledBid);

        const rowBg = row.companyInterviewWarning
          ? 'rgba(244,67,54,0.12)'
          : row.link.markedUselessAt
            ? 'rgba(97,97,97,0.12)'
            : row.duplicateCompanyRole || row.duplicateEarlierUrlBid
              ? 'rgba(255,193,7,0.08)'
              : 'transparent';

        if (bidReadyCompactLayout) {
          const existingBidIdForFeed = b?.id ?? null;
          return (
            <Box
              key={v.key}
              data-index={v.index}
              ref={virtualizer.measureElement}
              sx={{
                position: 'absolute',
                top: v.start,
                left: 0,
                width: '100%',
              }}
            >
              <Box
                sx={{
                  ...bidBoardRowGridSx,
                  borderBottom: 1,
                  borderColor: 'divider',
                  bgcolor: rowBg,
                }}
              >
                <Box
                  sx={{
                    ...bidBoardCellSx,
                    flexDirection: 'row',
                    alignItems: 'center',
                    gap: 0.5,
                    minWidth: 0,
                  }}
                >
                  <LinkCreatorBadge creator={row.link.createdBy} />
                  <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title={row.link.url}>
                    <Typography
                      component="a"
                      href={row.link.url}
                      target="_blank"
                      rel="noreferrer"
                      variant="body2"
                      sx={{
                        flex: '1 1 auto',
                        minWidth: 0,
                        maxWidth: '100%',
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                        color: 'primary.main',
                        textDecoration: 'underline',
                      }}
                  >
                    {displayUrl(row.link.url)}
                  </Typography>
                  </Tooltip>
                  <Box sx={{ flexShrink: 0, maxWidth: 120, minWidth: 0 }}>
                    <GroupBidderStack
                      bids={row.groupBidsOnLink ?? []}
                      currentUserId={currentUserId}
                      dense
                    />
                  </Box>
                  <UselessMarkChip
                    row={row}
                    currentUserId={currentUserId}
                    readOnly={readOnly}
                    patchLinkUseless={patchLinkUseless}
                  />
                  {currentUserId &&
                    row.link.createdByUserId === currentUserId &&
                    !readOnly &&
                    !row.link.markedUselessAt && (
                    <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="Mark posting as useless (everyone will see the Useless tag). Owner or auto-remove ≥10 min if allowed.">
                      <span>
                        <IconButton
                          size="small"
                          aria-label="Mark as useless"
                          disabled={patchLinkUseless.isPending}
                          onClick={() =>
                            patchLinkUseless.mutate({
                              linkId: row.link.id,
                              useless: true,
                            })
                          }
                          sx={(theme) => ({
                            width: 26,
                            height: 26,
                            p: 0.25,
                            color: 'error.main',
                            bgcolor: 'transparent',
                            '&:hover': {
                              bgcolor:
                                theme.palette.mode === 'dark'
                                  ? 'rgba(244,67,54,0.12)'
                                  : 'rgba(211,47,47,0.08)',
                            },
                          })}
                        >
                          <CloseIcon sx={{ fontSize: 16 }} />
                        </IconButton>
                      </span>
                    </Tooltip>
                  )}
                  <Stack
                    direction="row"
                    spacing={0.25}
                    alignItems="center"
                    flexShrink={0}
                    useFlexGap
                  >
                    {row.duplicateEarlierUrlBid && (
                      <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
                        placement="right"
                        title={
                          <EarlierBidTooltipBody
                            heading="Earlier bid (same job URL, another listing)"
                            detail={row.duplicateEarlierUrlBid}
                          />
                        }
                      >
                        <WarningAmberIcon fontSize="small" color="warning" />
                      </Tooltip>
                    )}
                    {row.duplicateCompanyRole && row.duplicateEarlierBid && (
                      <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
                        placement="right"
                        title={<CompanyRoleDupTooltip detail={row.duplicateEarlierBid} />}
                      >
                        <WarningAmberIcon fontSize="small" color="warning" />
                      </Tooltip>
                    )}
                    {row.companyInterviewWarning && (
                      <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="You already have an interview with this company — bidding again is risky.">
                        <WarningAmberIcon fontSize="small" color="error" />
                      </Tooltip>
                    )}
                  </Stack>
                </Box>
                <Box
                  sx={{
                    gridColumn: '2 / -1',
                    minWidth: 0,
                    width: '100%',
                    display: 'flex',
                    flexDirection: 'column',
                    justifyContent: 'center',
                    alignSelf: 'stretch',
                  }}
                >
                  {showFastFeed && (
                    <TextField
                      placeholder="resumeId, Co, Role, skills…"
                      value={fastFeed[row.link.id] ?? ''}
                      onChange={(e) =>
                        setFastFeed((prev) => ({ ...prev, [row.link.id]: e.target.value }))
                      }
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' && !e.shiftKey) {
                          e.preventDefault();
                          void commitFastFeed(row.link.id, existingBidIdForFeed);
                        }
                      }}
                      fullWidth
                      size="small"
                      sx={bidBoardFastFeedFieldSx}
                      inputProps={{ 'aria-label': 'Fast feed' }}
                    />
                  )}
                </Box>
              </Box>
            </Box>
          );
        }

        const stacks = (b.primaryStacks || []).join(', ');
        const rk = (suffix: string) => `${b.id}-${suffix}-${b.updatedAt}`;
        const fullRowBg = row.companyInterviewWarning
          ? 'rgba(244,67,54,0.12)'
          : row.link.markedUselessAt
            ? 'rgba(97,97,97,0.12)'
            : row.duplicateCompanyRole || row.duplicateEarlierUrlBid
              ? 'rgba(255,193,7,0.08)'
              : 'transparent';

        return (
          <Box
            key={v.key}
            data-index={v.index}
            ref={virtualizer.measureElement}
            sx={{
              position: 'absolute',
              top: v.start,
              left: 0,
              width: '100%',
            }}
          >
            <Box
              sx={{
                ...bidBoardRowGridSx,
                borderBottom: 1,
                borderColor: 'divider',
                bgcolor: fullRowBg,
              }}
            >
              <Box
                sx={{
                  ...bidBoardCellSx,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: 0.5,
                }}
              >
                <LinkCreatorBadge creator={row.link.createdBy} />
                <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title={row.link.url}>
                  <Typography
                    component="a"
                    href={row.link.url}
                    target="_blank"
                    rel="noreferrer"
                    variant="body2"
                    sx={{
                      flex: showFastFeed ? '1 1 32%' : '1 1 auto',
                      minWidth: 0,
                      overflow: 'hidden',
                      textOverflow: 'ellipsis',
                      whiteSpace: 'nowrap',
                      color: 'primary.main',
                      textDecoration: 'underline',
                    }}
                  >
                    {displayUrl(row.link.url)}
                  </Typography>
                </Tooltip>
                <Stack
                  direction="row"
                  spacing={0.25}
                  alignItems="center"
                  flexShrink={0}
                  useFlexGap
                >
                  {row.duplicateEarlierUrlBid && (
                    <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
                      placement="right"
                      title={
                        <EarlierBidTooltipBody
                          heading="Earlier bid (same job URL, another listing)"
                          detail={row.duplicateEarlierUrlBid}
                        />
                      }
                    >
                      <WarningAmberIcon fontSize="small" color="warning" />
                    </Tooltip>
                  )}
                  {row.duplicateCompanyRole && row.duplicateEarlierBid && (
                    <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
                      placement="right"
                      title={<CompanyRoleDupTooltip detail={row.duplicateEarlierBid} />}
                    >
                      <WarningAmberIcon fontSize="small" color="warning" />
                    </Tooltip>
                  )}
                  {row.companyInterviewWarning && (
                    <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="You already have an interview with this company — bidding again is risky.">
                      <WarningAmberIcon fontSize="small" color="error" />
                    </Tooltip>
                  )}
                  <UselessMarkChip
                    row={row}
                    currentUserId={currentUserId}
                    readOnly={readOnly}
                    patchLinkUseless={patchLinkUseless}
                  />
                </Stack>
                {showFastFeed && (
                  <TextField
                    placeholder="resumeId, Co, Role, skills…"
                    value={fastFeed[row.link.id] ?? ''}
                    onChange={(e) =>
                      setFastFeed((prev) => ({ ...prev, [row.link.id]: e.target.value }))
                    }
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' && !e.shiftKey) {
                        e.preventDefault();
                        void commitFastFeed(row.link.id, b.id);
                      }
                    }}
                    size="small"
                    sx={{ ...bidBoardFastFeedFieldSx, flex: '1 1 48%', minWidth: 0 }}
                    inputProps={{ 'aria-label': 'Fast feed' }}
                  />
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={b.resumeId}
                    key={rk('resume')}
                    onBlur={(e) =>
                      patchBid.mutate({ bidId: b.id, body: { resumeId: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    placeholder="Resume ID"
                    inputProps={{ 'aria-label': 'Resume ID' }}
                  />
                ) : (
                  <RoText>{b.resumeId}</RoText>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={b.company}
                    key={rk('company')}
                    onBlur={(e) =>
                      patchBid.mutate({ bidId: b.id, body: { company: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    placeholder="Company"
                    inputProps={{ 'aria-label': 'Company' }}
                  />
                ) : (
                  <RoText>{b.company}</RoText>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={b.role}
                    key={rk('role')}
                    onBlur={(e) =>
                      patchBid.mutate({ bidId: b.id, body: { role: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    placeholder="Role"
                    inputProps={{ 'aria-label': 'Role' }}
                  />
                ) : (
                  <RoText>{b.role}</RoText>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={stacks}
                    key={rk('stacks')}
                    onBlur={(e) =>
                      patchBid.mutate({
                        bidId: b.id,
                        body: {
                          primaryStacks: e.target.value
                            .split(',')
                            .map((s: string) => s.trim())
                            .filter(Boolean),
                        },
                      })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    placeholder="Stacks (comma-separated)"
                    inputProps={{ 'aria-label': 'Stacks' }}
                  />
                ) : stacks ? (
                  <RoText>{stacks}</RoText>
                ) : (
                  <RoText>—</RoText>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {readOnly ? (
                  <FormatStatusBadge kind="bid" status={b.status} />
                ) : (
                  <TextField
                    select
                    value={b.status}
                    onChange={(e) =>
                      patchBid.mutate({ bidId: b.id, body: { status: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSx}
                    SelectProps={{ displayEmpty: true }}
                    inputProps={{ 'aria-label': 'Status' }}
                  >
                    {BID_STATUSES.map((s) => (
                      <MenuItem key={s} value={s}>
                        {s}
                      </MenuItem>
                    ))}
                  </TextField>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={b.origin}
                    key={rk('origin')}
                    onBlur={(e) =>
                      patchBid.mutate({ bidId: b.id, body: { origin: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    placeholder="e.g. LinkedIn"
                    inputProps={{ 'aria-label': 'Origin' }}
                  />
                ) : (
                  <RoText>{b.origin}</RoText>
                )}
              </Box>
              <Box sx={{ ...bidBoardCellSx, minWidth: 52 }}>
                <GroupBidderStack
                  bids={row.groupBidsOnLink ?? []}
                  currentUserId={currentUserId}
                />
              </Box>
              <Box
                sx={{
                  ...bidBoardCellSx,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: 0,
                  width: '100%',
                  maxWidth: '100%',
                  px: 0.25,
                }}
              >
                {rowEditing ? (
                  <>
                    <TextField
                      defaultValue={mergedJdText(row, b)}
                      key={`jd-${b.id}-${b.updatedAt}-${row.link.updatedAt ?? row.link.createdAt}`}
                      onBlur={(e) =>
                        patchBid.mutate({ bidId: b.id, body: { jobDescription: e.target.value } })
                      }
                      fullWidth
                      size="small"
                      sx={{ ...bidBoardTextFieldSingleLineEllipsisSx, flex: '1 1 auto', minWidth: 0 }}
                      placeholder="Job description"
                      inputProps={{ 'aria-label': 'Job description' }}
                      inputRef={(el) => {
                        if (el) jdInputRefs.current.set(b.id, el);
                        else jdInputRefs.current.delete(b.id);
                      }}
                    />
                    <CellCopyButton
                      label="job description"
                      disableWhenEmpty={false}
                      getText={() =>
                        jdInputRefs.current.get(b.id)?.value ?? mergedJdText(row, b)
                      }
                    />
                  </>
                ) : (
                  <>
                    <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
                      describeChild
                      title={
                        <Box sx={{ maxWidth: '100%', whiteSpace: 'pre-wrap', typography: 'caption' }}>
                          {mergedJdText(row, b).trim() ? mergedJdText(row, b) : '—'}
                        </Box>
                      }
                    >
                      <Typography
                        variant="caption"
                        noWrap
                        sx={{
                          flex: '1 1 auto',
                          minWidth: 0,
                          cursor: 'help',
                          textAlign: 'center',
                          fontWeight: 600,
                        }}
                      >
                        {jdAttachedLabel(row, b)}
                      </Typography>
                    </Tooltip>
                    <CellCopyButton
                      label="job description"
                      getText={() => mergedJdText(row, b)}
                    />
                  </>
                )}
              </Box>
              <Box
                sx={{
                  ...bidBoardCellSx,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: 0,
                  width: '100%',
                  maxWidth: '100%',
                  px: 0.25,
                }}
              >
                {rowEditing ? (
                  <>
                    <TextField
                      defaultValue={b.gptResumeContent}
                      key={rk('gpt')}
                      onBlur={(e) =>
                        patchBid.mutate({ bidId: b.id, body: { gptResumeContent: e.target.value } })
                      }
                      fullWidth
                      size="small"
                      sx={{ ...bidBoardTextFieldSingleLineEllipsisSx, flex: '1 1 auto', minWidth: 0 }}
                      placeholder="GPT resume"
                      inputProps={{ 'aria-label': 'GPT resume text' }}
                      inputRef={(el) => {
                        if (el) gptResumeInputRefs.current.set(b.id, el);
                        else gptResumeInputRefs.current.delete(b.id);
                      }}
                    />
                    <CellCopyButton
                      label="GPT resume"
                      disableWhenEmpty={false}
                      getText={() =>
                        gptResumeInputRefs.current.get(b.id)?.value ?? b.gptResumeContent
                      }
                    />
                  </>
                ) : (
                  <>
                    <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
                      describeChild
                      title={
                        <Box
                          sx={{
                            maxWidth: 520,
                            maxHeight: 480,
                            overflow: 'auto',
                            whiteSpace: 'pre-wrap',
                            typography: 'caption',
                            lineHeight: 1.45,
                          }}
                        >
                          {composeResume(myProfile, b.gptResumeContent ?? '') ?? '—'}
                        </Box>
                      }
                    >
                      <Typography
                        variant="caption"
                        noWrap
                        sx={{
                          flex: '1 1 auto',
                          minWidth: 0,
                          cursor: 'help',
                          textAlign: 'center',
                          fontWeight: 600,
                        }}
                      >
                        {gptResumeAttachedLabel(b)}
                      </Typography>
                    </Tooltip>
                    <CellCopyButton
                      label="GPT resume"
                      getText={() => composeResume(myProfile, b.gptResumeContent ?? '') ?? b.gptResumeContent}
                    />
                  </>
                )}
              </Box>
              <Box
                sx={{
                  ...bidBoardCellSx,
                  flexDirection: 'row',
                  alignItems: 'center',
                  gap: 0,
                  width: '100%',
                  maxWidth: '100%',
                  px: 0.25,
                }}
              >
                {rowEditing ? (
                  <>
                    <TextField
                      defaultValue={b.comment}
                      key={rk('cm')}
                      onBlur={(e) =>
                        patchBid.mutate({ bidId: b.id, body: { comment: e.target.value } })
                      }
                      fullWidth
                      size="small"
                      sx={{ ...bidBoardTextFieldSingleLineEllipsisSx, flex: '1 1 auto', minWidth: 0 }}
                      placeholder="Comment"
                      inputProps={{ 'aria-label': 'Comment' }}
                      inputRef={(el) => {
                        if (el) commentInputRefs.current.set(b.id, el);
                        else commentInputRefs.current.delete(b.id);
                      }}
                    />
                    <CellCopyButton
                      label="comment"
                      disableWhenEmpty={false}
                      getText={() => commentInputRefs.current.get(b.id)?.value ?? b.comment}
                    />
                  </>
                ) : (
                  <>
                    <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
                      describeChild
                      title={
                        <Box sx={{ maxWidth: '100%', whiteSpace: 'pre-wrap', typography: 'caption' }}>
                          {(b.comment || '').trim() ? b.comment : '—'}
                        </Box>
                      }
                    >
                      <Typography
                        variant="caption"
                        noWrap
                        sx={{
                          flex: '1 1 auto',
                          minWidth: 0,
                          cursor: 'help',
                          textAlign: 'center',
                          fontWeight: 600,
                        }}
                      >
                        {commentAttachedLabel(b)}
                      </Typography>
                    </Tooltip>
                    <CellCopyButton label="comment" getText={() => b.comment} />
                  </>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                <Tooltip {...BID_BOARD_TOOLTIP_COMMON}
                  title={`Created (fixed): ${new Date(b.firstCreatedAt ?? b.updatedAt).toLocaleString()}\nLast edit: ${new Date(b.updatedAt).toLocaleString()} · ${b.lastModifiedBy?.nickname ?? '—'}`}
                >
                  <Typography
                    variant="caption"
                    noWrap
                    component="div"
                    sx={{
                      width: '100%',
                      overflow: 'hidden',
                      textOverflow: 'ellipsis',
                      textAlign: 'center',
                      color: 'text.secondary',
                    }}
                  >
                    <Box component="span" sx={{ display: 'block', fontWeight: 500 }}>
                      {new Date(b.firstCreatedAt ?? b.updatedAt).toLocaleDateString(undefined, {
                        month: 'short',
                        day: 'numeric',
                      })}
                    </Box>
                    <Box component="span" sx={{ opacity: 0.85, fontSize: '0.7rem' }}>
                      {b.lastModifiedBy?.nickname ?? '—'} ·{' '}
                      {new Date(b.updatedAt).toLocaleTimeString(undefined, {
                        hour: 'numeric',
                        minute: '2-digit',
                      })}
                    </Box>
                  </Typography>
                </Tooltip>
              </Box>
              <Box
                sx={{
                  ...bidBoardStickyActionsSx,
                  display: 'flex',
                  justifyContent: 'center',
                  alignItems: 'center',
                  minWidth: 0,
                  width: '100%',
                }}
              >
                <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="Row actions">
                  <IconButton
                    size="small"
                    aria-label="Open row actions"
                    aria-haspopup="true"
                    aria-expanded={bidActionsMenu?.bidId === b.id ? 'true' : undefined}
                    onClick={(e) =>
                      setBidActionsMenu(
                        bidActionsMenu?.bidId === b.id ? null : { bidId: b.id, anchor: e.currentTarget }
                      )
                    }
                  >
                    <MoreVertIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              </Box>
            </Box>
            <Collapse in={Boolean(b && expandedBidId === b.id)}>
              {b ? (
                <Box
                  sx={{
                    p: 2,
                    bgcolor: 'background.default',
                    borderBottom: 1,
                    borderColor: 'divider',
                  }}
                >
                  <Stack direction={{ xs: 'column', md: 'row' }} spacing={1}>
                    <TextField
                      value={ivDraft.meetingLink}
                      onChange={(e) =>
                        setIvDraft((d) => ({ ...d, meetingLink: e.target.value }))
                      }
                      fullWidth
                      size="small"
                      required
                      placeholder="Meeting link"
                      inputProps={{ 'aria-label': 'Meeting link' }}
                    />
                    <TextField
                      type="date"
                      value={ivDraft.scheduledDate}
                      onChange={(e) =>
                        setIvDraft((d) => ({ ...d, scheduledDate: e.target.value }))
                      }
                      size="small"
                      inputProps={{ 'aria-label': 'Interview date' }}
                    />
                    <TextField
                      type="time"
                      InputLabelProps={{ shrink: true }}
                      label="Time"
                      value={toHtmlTimeInputValue(ivDraft.scheduledTime)}
                      onChange={(e) =>
                        setIvDraft((d) => ({ ...d, scheduledTime: e.target.value }))
                      }
                      size="small"
                      slotProps={{
                        htmlInput: { step: 60, 'aria-label': 'Interview time' },
                      }}
                      sx={{ minWidth: 108 }}
                    />
                    <TextField
                      value={ivDraft.recruiter}
                      onChange={(e) =>
                        setIvDraft((d) => ({ ...d, recruiter: e.target.value }))
                      }
                      size="small"
                      placeholder="Recruiter"
                      inputProps={{ 'aria-label': 'Recruiter' }}
                    />
                    <Button
                      onClick={() => createInterview.mutate(b.id)}
                      disabled={!ivDraft.meetingLink.trim() || createInterview.isPending}
                    >
                      Create interview
                    </Button>
                  </Stack>
                </Box>
              ) : null}
            </Collapse>
          </Box>
        );
      })}
    </Box>
    <Menu
      anchorEl={bidActionsMenu?.anchor ?? null}
      open={Boolean(bidActionsMenu)}
      onClose={closeBidActionsMenu}
      anchorOrigin={{ vertical: 'center', horizontal: 'left' }}
      transformOrigin={{ vertical: 'center', horizontal: 'right' }}
      slotProps={{ paper: { sx: { minWidth: 220 } } }}
    >
      <MenuItem
        disabled={readOnly}
        onClick={() => {
          const id = bidActionsMenu?.bidId;
          closeBidActionsMenu();
          if (!id || readOnly) return;
          if (editingBidId === id) finishEditing();
          else setEditingBidId(id);
        }}
      >
        <ListItemIcon>
          {bidActionsMenu && editingBidId === bidActionsMenu.bidId ? (
            <CheckIcon fontSize="small" />
          ) : (
            <EditIcon fontSize="small" />
          )}
        </ListItemIcon>
        <ListItemText
          primary={bidActionsMenu && editingBidId === bidActionsMenu.bidId ? 'Done editing' : 'Edit row'}
        />
      </MenuItem>
      {bidActionsMenu &&
        (() => {
          const menuRow = rows.find((r) => r.myBid?.id === bidActionsMenu.bidId);
          if (
            !menuRow ||
            readOnly ||
            String(menuRow.link.createdByUserId) !== String(currentUserId ?? '')
          ) {
            return null;
          }
          const marked = Boolean(menuRow.link.markedUselessAt);
          return (
            <MenuItem
              disabled={patchLinkUseless.isPending}
              onClick={() => {
                closeBidActionsMenu();
                patchLinkUseless.mutate({
                  linkId: menuRow.link.id,
                  useless: !marked,
                });
              }}
            >
              <ListItemIcon>
                <CloseIcon fontSize="small" sx={{ color: 'error.main' }} />
              </ListItemIcon>
              <ListItemText
                primary={marked ? 'Clear useless mark' : 'Mark link as useless'}
                secondary={
                  marked
                    ? undefined
                    : 'Everyone sees a Useless tag; owner or auto-remove ≥10 min if allowed'
                }
                secondaryTypographyProps={{ variant: 'caption' }}
              />
            </MenuItem>
          );
        })()}
      <MenuItem
        disabled={readOnly || deleteBid.isPending}
        onClick={() => {
          const id = bidActionsMenu?.bidId;
          closeBidActionsMenu();
          if (!id || readOnly) return;
          if (
            !window.confirm(
              'Remove only your bid on this job? The shared group link stays in the list for everyone else.'
            )
          ) {
            return;
          }
          deleteBid.mutate(id);
          if (expandedBidId === id) setExpandedBid(null);
        }}
      >
        <ListItemIcon>
          <DeleteOutlineIcon fontSize="small" color="error" />
        </ListItemIcon>
        <ListItemText primary="Remove bid" primaryTypographyProps={{ color: 'error' }} />
      </MenuItem>
      <MenuItem
        disabled={readOnly}
        onClick={() => {
          const id = bidActionsMenu?.bidId;
          closeBidActionsMenu();
          if (!id || readOnly) return;
          setExpandedBid(expandedBidId === id ? null : id);
        }}
      >
        <ListItemIcon>
          <EventIcon fontSize="small" />
        </ListItemIcon>
        <ListItemText
          primary={
            bidActionsMenu && expandedBidId === bidActionsMenu.bidId
              ? 'Hide interview form'
              : 'Schedule interview'
          }
        />
      </MenuItem>
    </Menu>
    </>
  );
}
