import type { Theme } from '@mui/material/styles';

/** 13 columns: link, resume, company, role, stacks, status, origin, bidders, JD, GPT resume, comment, modified, actions */
export const BID_GRID_COLS =
  'minmax(140px,1.25fr) minmax(88px,0.75fr) minmax(100px,1fr) minmax(100px,1fr) minmax(130px,1.1fr) minmax(112px,0.95fr) minmax(88px,0.8fr) minmax(108px,0.95fr) minmax(56px,64px) minmax(56px,64px) minmax(56px,64px) minmax(96px,0.75fr) minmax(72px, auto)';

/** Sticky right column (actions) while scrolling horizontally. */
export const bidBoardStickyActionsSx = {
  position: 'sticky',
  right: 0,
  zIndex: 3,
  bgcolor: (theme: Theme) =>
    theme.palette.mode === 'dark' ? 'rgba(100, 181, 246, 0.14)' : 'rgba(25, 118, 210, 0.1)',
  boxShadow: '-10px 0 14px -6px rgba(0,0,0,0.18)',
};

/** Same grid metrics for sticky header, composer strip, and bid rows so columns line up. */
export const bidBoardRowGridSx = {
  display: 'grid',
  gridTemplateColumns: BID_GRID_COLS,
  gap: 0.5,
  alignItems: 'center',
  py: 0.35,
  px: 0.5,
  width: '100%',
  boxSizing: 'border-box',
} as const;

/** Center column content horizontally (and stack children in the middle). */
export const bidBoardCellSx = {
  display: 'flex',
  flexDirection: 'column',
  justifyContent: 'center',
  alignItems: 'center',
  textAlign: 'center',
  minWidth: 0,
  width: '100%',
  overflow: 'hidden',
} as const;

/** Center text inside row TextFields so it matches read-only Typography cells. */
export const bidBoardTextFieldSx = {
  alignSelf: 'stretch',
  width: '100%',
  '& .MuiOutlinedInput-input, & .MuiInputBase-input': {
    textAlign: 'center',
  },
  '& .MuiSelect-select': {
    textAlign: 'center',
  },
} as const;

/** Single-line inputs with ellipsis; keeps row height minimal. */
export const bidBoardTextFieldSingleLineEllipsisSx = {
  ...bidBoardTextFieldSx,
  '& .MuiOutlinedInput-root': { overflow: 'hidden' },
  '& .MuiOutlinedInput-input, & .MuiInputBase-input': {
    textAlign: 'center',
    overflow: 'hidden !important',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
} as const;

/** Fast-feed column: left-aligned single line + ellipsis. */
export const bidBoardFastFeedFieldSx = {
  alignSelf: 'stretch',
  width: '100%',
  '& .MuiOutlinedInput-root': { overflow: 'hidden' },
  '& .MuiOutlinedInput-input, & .MuiInputBase-input': {
    textAlign: 'left',
    overflow: 'hidden !important',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
} as const;

export type BidSortField =
  | 'linkCreatedAt'
  | 'url'
  | 'resumeId'
  | 'company'
  | 'role'
  | 'status'
  | 'origin'
  | 'jobDescription'
  | 'bidUpdatedAt';
