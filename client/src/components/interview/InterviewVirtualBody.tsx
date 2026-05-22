import { useMemo, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import {
  Box,
  Button,
  Collapse,
  IconButton,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import AddCircleOutlineIcon from '@mui/icons-material/AddCircleOutline';
import CheckIcon from '@mui/icons-material/Check';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import DescriptionIcon from '@mui/icons-material/Description';
import ArticleIcon from '@mui/icons-material/Article';
import { BidContentViewer } from '../bid/BidContentViewer';
import EditIcon from '@mui/icons-material/Edit';
import WarningAmberIcon from '@mui/icons-material/WarningAmber';
import type { Dispatch, ReactNode, SetStateAction } from 'react';
import {
  bidBoardCellSx,
  bidBoardRowGridSx,
  bidBoardTextFieldSingleLineEllipsisSx,
  bidBoardTextFieldSx,
} from '../bid/bidBoardGrid';
import { INTERVIEW_GRID_COLS, interviewMeetingLinkFieldSx } from './interviewGrid';
import { toHtmlTimeInputValue } from '../../utils/timeInput';
import { FormatStatusBadge } from '../FormatStatusBadge';
import { BID_BOARD_TOOLTIP_COMMON } from '../../utils/bidBoardTooltip';

const IV_TYPES = ['HR', 'ASSESSMENT', 'TECH_1', 'TECH_2', 'TECH_3', 'CLIENT', 'OFFER'] as const;
const IV_STATUSES = ['scheduled', 'completed', 'passed', 'failed', 'cancelled'] as const;

export type InterviewRowType = {
  _id: string;
  meetingLink: string;
  origin: string;
  bidId?: string | null;
  interviewType: string;
  company: string;
  role: string;
  recruiter: string;
  additionalAttendees: string;
  scheduledDate: string | null;
  scheduledTime: string;
  durationMinutes: number;
  status: string;
  userComment: string;
  parentInterviewId?: string | null;
  recruiterCompanyDuplicateWarning?: boolean;
  createdAt: string;
  updatedAt?: string;
  /** Snapshotted at create time so callers see stable context across edits. */
  attachedJobDescription?: string;
  attachedResumeContent?: string;
};

type MutPatch = (payload: { id: string; body: Record<string, unknown> }) => void;

function meetingHref(link: string) {
  const t = link.trim();
  if (!t) return undefined;
  if (/^https?:\/\//i.test(t)) return t;
  return `https://${t}`;
}

function RoCell({ children, title }: { children: ReactNode; title?: string }) {
  return (
    <Typography
      variant="body2"
      noWrap
      component="div"
      title={title}
      sx={{
        width: '100%',
        maxWidth: '100%',
        textAlign: 'center',
        overflow: 'hidden',
        textOverflow: 'ellipsis',
      }}
    >
      {children}
    </Typography>
  );
}

type Props = {
  scrollElement: HTMLDivElement | null;
  rows: InterviewRowType[];
  expandedId: string | null;
  setExpandedId: (id: string | null) => void;
  patchIv: { mutate: MutPatch };
  createNext: { mutate: (body: Record<string, unknown>) => void; isPending: boolean };
  deleteInterview: { mutate: (id: string) => void; isPending: boolean };
  nextDraft: {
    meetingLink: string;
    interviewType: string;
    scheduledDate: string;
    scheduledTime: string;
  };
  setNextDraft: Dispatch<
    SetStateAction<{
      meetingLink: string;
      interviewType: string;
      scheduledDate: string;
      scheduledTime: string;
    }>
  >;
  allowedNextTypes: string[];
};

function rk(row: InterviewRowType, suffix: string, editing: boolean) {
  return `${row._id}-${suffix}-${row.updatedAt ?? row.createdAt}-${editing ? 'e' : 'v'}`;
}

type GroupedListItem =
  | { kind: 'group'; roleLabel: string; key: string }
  | { kind: 'row'; row: InterviewRowType };

function buildGroupedInterviewList(rows: InterviewRowType[]): GroupedListItem[] {
  const sorted = [...rows].sort((a, b) => {
    const keyA = (a.role || '').trim().toLowerCase() || '\uffff';
    const keyB = (b.role || '').trim().toLowerCase() || '\uffff';
    const rc = keyA.localeCompare(keyB, undefined, { sensitivity: 'base' });
    if (rc !== 0) return rc;
    const ta = a.scheduledDate ? new Date(a.scheduledDate).getTime() : 0;
    const tb = b.scheduledDate ? new Date(b.scheduledDate).getTime() : 0;
    if (tb !== ta) return tb - ta;
    const ca = new Date(a.createdAt).getTime();
    const cb = new Date(b.createdAt).getTime();
    if (cb !== ca) return cb - ca;
    return String(b._id).localeCompare(String(a._id));
  });

  const out: GroupedListItem[] = [];
  let prevKey: string | null = null;
  for (const row of sorted) {
    const gKey = (row.role || '').trim().toLowerCase() || '__no_role__';
    if (gKey !== prevKey) {
      const roleLabel = (row.role || '').trim() || '— No role';
      out.push({ kind: 'group', roleLabel, key: `grp-${gKey}-${row._id}` });
      prevKey = gKey;
    }
    out.push({ kind: 'row', row });
  }
  return out;
}

export function InterviewVirtualBody({
  scrollElement,
  rows,
  expandedId,
  setExpandedId,
  patchIv,
  createNext,
  deleteInterview,
  nextDraft,
  setNextDraft,
  allowedNextTypes,
}: Props) {
  const [editingInterviewId, setEditingInterviewId] = useState<string | null>(null);
  /** Modal viewer for the snapshotted JD + resume attached at interview-create time. */
  const [viewer, setViewer] = useState<{
    title: string;
    subtitle?: string;
    body: string;
    copyLabel: string;
    serif?: boolean;
  } | null>(null);

  const finishEditing = () => {
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    setEditingInterviewId(null);
  };

  const items = useMemo(() => buildGroupedInterviewList(rows), [rows]);

  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => scrollElement,
    estimateSize: (index) => (items[index]?.kind === 'group' ? 30 : 44),
    overscan: 10,
    getItemKey: (index) => {
      const it = items[index];
      if (!it) return String(index);
      return it.kind === 'group' ? it.key : it.row._id;
    },
  });

  const vitems = virtualizer.getVirtualItems();

  return (
    <>
    <Box sx={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
      {vitems.map((v) => {
        const item = items[v.index];
        if (!item) return null;

        if (item.kind === 'group') {
          return (
            <Box
              key={v.key}
              data-index={v.index}
              ref={virtualizer.measureElement}
              sx={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: '100%',
                transform: `translateY(${v.start}px)`,
              }}
            >
              <Box
                sx={{
                  width: '100%',
                  px: 1,
                  py: 0.5,
                  bgcolor: 'action.selected',
                  borderBottom: 1,
                  borderColor: 'divider',
                  boxSizing: 'border-box',
                }}
              >
                <Typography variant="caption" fontWeight={700} color="text.secondary">
                  Role · {item.roleLabel}
                </Typography>
              </Box>
            </Box>
          );
        }

        const row = item.row;
        const rowEditing = editingInterviewId === row._id;
        const href = meetingHref(row.meetingLink);

        return (
          <Box
            key={v.key}
            data-index={v.index}
            ref={virtualizer.measureElement}
            sx={{
              position: 'absolute',
              top: 0,
              left: 0,
              width: '100%',
              transform: `translateY(${v.start}px)`,
            }}
          >
            <Box
              sx={{
                ...bidBoardRowGridSx,
                gridTemplateColumns: INTERVIEW_GRID_COLS,
                borderBottom: 1,
                borderColor: 'divider',
                bgcolor: row.recruiterCompanyDuplicateWarning
                  ? 'rgba(255,193,7,0.1)'
                  : 'transparent',
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
                {row.recruiterCompanyDuplicateWarning && (
                  <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="Same company + recruiter as an earlier interview.">
                    <WarningAmberIcon fontSize="small" color="warning" sx={{ flexShrink: 0 }} />
                  </Tooltip>
                )}
                {rowEditing ? (
                  <TextField
                    defaultValue={row.meetingLink}
                    key={rk(row, 'ml', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({ id: row._id, body: { meetingLink: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    placeholder="Meeting link"
                    sx={{ ...interviewMeetingLinkFieldSx, flex: '1 1 auto', minWidth: 0 }}
                    inputProps={{ 'aria-label': 'Meeting link' }}
                  />
                ) : (
                  <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title={row.meetingLink || ''}>
                    <Typography
                      component={href ? 'a' : 'span'}
                      href={href}
                      target={href ? '_blank' : undefined}
                      rel={href ? 'noreferrer' : undefined}
                      variant="body2"
                      sx={{
                        flex: '1 1 auto',
                        minWidth: 0,
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                        color: 'primary.main',
                        textDecoration: href ? 'underline' : 'none',
                        textAlign: 'center',
                        width: '100%',
                      }}
                    >
                      {row.meetingLink?.trim() ? row.meetingLink : '—'}
                    </Typography>
                  </Tooltip>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    select
                    defaultValue={row.interviewType}
                    key={rk(row, 'tp', rowEditing)}
                    onChange={(e) =>
                      patchIv.mutate({ id: row._id, body: { interviewType: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSx}
                    SelectProps={{ displayEmpty: true }}
                  >
                    {IV_TYPES.map((t) => (
                      <MenuItem key={t} value={t}>
                        {t}
                      </MenuItem>
                    ))}
                  </TextField>
                ) : (
                  <RoCell>{row.interviewType}</RoCell>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={row.company}
                    key={rk(row, 'co', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({ id: row._id, body: { company: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    inputProps={{ 'aria-label': 'Company' }}
                  />
                ) : (
                  <RoCell title={row.company}>{row.company || '—'}</RoCell>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={row.role}
                    key={rk(row, 'rl', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({ id: row._id, body: { role: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    inputProps={{ 'aria-label': 'Role' }}
                  />
                ) : (
                  <RoCell title={row.role}>{row.role || '—'}</RoCell>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={row.recruiter}
                    key={rk(row, 'rc', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({ id: row._id, body: { recruiter: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    inputProps={{ 'aria-label': 'Recruiter' }}
                  />
                ) : (
                  <RoCell title={row.recruiter}>{row.recruiter || '—'}</RoCell>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={row.additionalAttendees}
                    key={rk(row, 'at', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({
                        id: row._id,
                        body: { additionalAttendees: e.target.value },
                      })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    inputProps={{ 'aria-label': 'Attendees' }}
                  />
                ) : (
                  <RoCell title={row.additionalAttendees}>{row.additionalAttendees || '—'}</RoCell>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    type="date"
                    InputLabelProps={{ shrink: true }}
                    defaultValue={row.scheduledDate ? row.scheduledDate.slice(0, 10) : ''}
                    key={rk(row, 'dt', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({
                        id: row._id,
                        body: { scheduledDate: e.target.value || null },
                      })
                    }
                    size="small"
                    sx={bidBoardTextFieldSx}
                    inputProps={{ 'aria-label': 'Date' }}
                  />
                ) : (
                  <RoCell>
                    {row.scheduledDate
                      ? new Date(row.scheduledDate).toLocaleDateString(undefined, {
                          year: 'numeric',
                          month: 'short',
                          day: 'numeric',
                        })
                      : '—'}
                  </RoCell>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    type="time"
                    InputLabelProps={{ shrink: true }}
                    label="Time"
                    defaultValue={toHtmlTimeInputValue(row.scheduledTime)}
                    key={rk(row, 'tm', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({ id: row._id, body: { scheduledTime: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={{ ...bidBoardTextFieldSx, minWidth: 0 }}
                    slotProps={{
                      htmlInput: { step: 60, 'aria-label': 'Time' },
                    }}
                  />
                ) : (
                  <RoCell>
                    {toHtmlTimeInputValue(row.scheduledTime) || row.scheduledTime?.trim() || '—'}
                  </RoCell>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    type="number"
                    defaultValue={row.durationMinutes}
                    key={rk(row, 'du', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({
                        id: row._id,
                        body: { durationMinutes: Number(e.target.value) || 0 },
                      })
                    }
                    size="small"
                    sx={bidBoardTextFieldSx}
                    inputProps={{ 'aria-label': 'Duration minutes' }}
                  />
                ) : (
                  <RoCell>{row.durationMinutes}</RoCell>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    select
                    defaultValue={row.status}
                    key={rk(row, 'st', rowEditing)}
                    onChange={(e) =>
                      patchIv.mutate({ id: row._id, body: { status: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSx}
                    SelectProps={{ displayEmpty: true }}
                  >
                    {IV_STATUSES.map((s) => (
                      <MenuItem key={s} value={s}>
                        {s}
                      </MenuItem>
                    ))}
                  </TextField>
                ) : (
                  <Box
                    sx={{
                      display: 'flex',
                      justifyContent: 'center',
                      alignItems: 'center',
                      width: '100%',
                      minWidth: 0,
                    }}
                  >
                    <FormatStatusBadge kind="interview" status={row.status} />
                  </Box>
                )}
              </Box>
              <Box sx={bidBoardCellSx}>
                {rowEditing ? (
                  <TextField
                    defaultValue={row.userComment}
                    key={rk(row, 'cm', rowEditing)}
                    onBlur={(e) =>
                      patchIv.mutate({ id: row._id, body: { userComment: e.target.value } })
                    }
                    fullWidth
                    size="small"
                    sx={bidBoardTextFieldSingleLineEllipsisSx}
                    inputProps={{ 'aria-label': 'Comment' }}
                  />
                ) : (
                  <RoCell title={row.userComment}>{row.userComment || '—'}</RoCell>
                )}
              </Box>
              <Box
                sx={{
                  display: 'flex',
                  justifyContent: 'flex-end',
                  alignItems: 'center',
                  gap: 0.25,
                  minWidth: 0,
                  width: '100%',
                }}
              >
                <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title={rowEditing ? 'Done editing' : 'Edit row'}>
                  <IconButton
                    size="small"
                    aria-label={rowEditing ? 'Done editing' : 'Edit row'}
                    color={rowEditing ? 'primary' : 'default'}
                    onClick={() => (rowEditing ? finishEditing() : setEditingInterviewId(row._id))}
                  >
                    {rowEditing ? <CheckIcon fontSize="small" /> : <EditIcon fontSize="small" />}
                  </IconButton>
                </Tooltip>
                <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="Remove interview">
                  <span>
                    <IconButton
                      size="small"
                      aria-label="Remove interview"
                      color="error"
                      disabled={deleteInterview.isPending}
                      onClick={() => {
                        if (
                          !window.confirm(
                            'Delete this interview and any follow-up stages under it? This cannot be undone.'
                          )
                        ) {
                          return;
                        }
                        if (expandedId === row._id) setExpandedId(null);
                        if (editingInterviewId === row._id) setEditingInterviewId(null);
                        deleteInterview.mutate(row._id);
                      }}
                    >
                      <DeleteOutlineIcon fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
                <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="View attached job description">
                  <span>
                    <IconButton
                      size="small"
                      aria-label="View attached job description"
                      disabled={!(row.attachedJobDescription || '').trim()}
                      onClick={() =>
                        setViewer({
                          title: 'Job description',
                          subtitle:
                            [row.company, row.role].filter(Boolean).join(' · ') || undefined,
                          body: row.attachedJobDescription || '',
                          copyLabel: 'job description',
                        })
                      }
                    >
                      <DescriptionIcon fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
                <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="View attached resume">
                  <span>
                    <IconButton
                      size="small"
                      aria-label="View attached resume"
                      disabled={!(row.attachedResumeContent || '').trim()}
                      onClick={() =>
                        setViewer({
                          title: 'Resume',
                          subtitle:
                            [row.company, row.role].filter(Boolean).join(' · ') || undefined,
                          body: row.attachedResumeContent || '',
                          copyLabel: 'resume',
                          serif: true,
                        })
                      }
                    >
                      <ArticleIcon fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
                <Tooltip {...BID_BOARD_TOOLTIP_COMMON} title="Add next interview stage">
                  <IconButton
                    size="small"
                    aria-label="Add next interview stage"
                    onClick={() =>
                      setExpandedId(expandedId === row._id ? null : row._id)
                    }
                  >
                    <AddCircleOutlineIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              </Box>
            </Box>
            <Collapse in={expandedId === row._id}>
              <Box
                sx={{
                  p: 2,
                  bgcolor: 'background.default',
                  borderBottom: 1,
                  borderColor: 'divider',
                }}
              >
                <Typography variant="subtitle2" gutterBottom>
                  Next stage
                </Typography>
                <Stack direction={{ xs: 'column', md: 'row' }} spacing={1}>
                  <TextField
                    value={nextDraft.meetingLink}
                    onChange={(e) =>
                      setNextDraft((d) => ({ ...d, meetingLink: e.target.value }))
                    }
                    fullWidth
                    size="small"
                    required
                    placeholder="Meeting link"
                    inputProps={{ 'aria-label': 'Next meeting link' }}
                  />
                  <TextField
                    select
                    value={nextDraft.interviewType}
                    onChange={(e) =>
                      setNextDraft((d) => ({ ...d, interviewType: e.target.value }))
                    }
                    size="small"
                    sx={{ minWidth: 160 }}
                    inputProps={{ 'aria-label': 'Interview type' }}
                  >
                    {(
                      allowedNextTypes.length
                        ? allowedNextTypes
                        : ['ASSESSMENT', 'TECH_1', 'TECH_2', 'TECH_3', 'CLIENT', 'OFFER']
                    ).map(
                      (t) => (
                        <MenuItem key={t} value={t}>
                          {t}
                        </MenuItem>
                      )
                    )}
                  </TextField>
                  <TextField
                    type="date"
                    InputLabelProps={{ shrink: true }}
                    value={nextDraft.scheduledDate}
                    onChange={(e) =>
                      setNextDraft((d) => ({ ...d, scheduledDate: e.target.value }))
                    }
                    size="small"
                    inputProps={{ 'aria-label': 'Interview date' }}
                  />
                  <TextField
                    type="time"
                    InputLabelProps={{ shrink: true }}
                    label="Time"
                    value={toHtmlTimeInputValue(nextDraft.scheduledTime)}
                    onChange={(e) =>
                      setNextDraft((d) => ({ ...d, scheduledTime: e.target.value }))
                    }
                    size="small"
                    slotProps={{
                      htmlInput: { step: 60, 'aria-label': 'Interview time' },
                    }}
                    sx={{ minWidth: 108 }}
                  />
                  <Button
                    variant="contained"
                    disabled={!nextDraft.meetingLink.trim() || createNext.isPending}
                    onClick={() =>
                      createNext.mutate({
                        meetingLink: nextDraft.meetingLink,
                        origin: 'bid',
                        interviewType: nextDraft.interviewType,
                        parentInterviewId: row._id,
                        ...(row.bidId ? { bidId: row.bidId } : {}),
                        scheduledDate: nextDraft.scheduledDate || undefined,
                        scheduledTime: nextDraft.scheduledTime,
                      })
                    }
                  >
                    Create next
                  </Button>
                </Stack>
              </Box>
            </Collapse>
          </Box>
        );
      })}
    </Box>
    <BidContentViewer
      open={Boolean(viewer)}
      onClose={() => setViewer(null)}
      title={viewer?.title ?? ''}
      subtitle={viewer?.subtitle}
      body={viewer?.body ?? ''}
      copyLabel={viewer?.copyLabel ?? 'content'}
      serif={viewer?.serif}
    />
    </>
  );
}
