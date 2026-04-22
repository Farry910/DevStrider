import { bidBoardTextFieldSingleLineEllipsisSx } from '../bid/bidBoardGrid';

/** One fewer column than before: origin removed (bid-only workflow). */
export const INTERVIEW_GRID_COLS =
  'minmax(180px,1.5fr) minmax(100px,0.85fr) minmax(100px,1fr) minmax(100px,1fr) minmax(104px,1fr) minmax(120px,1.05fr) minmax(110px,0.95fr) minmax(84px,0.7fr) minmax(72px,0.65fr) minmax(112px,0.95fr) minmax(140px,1.1fr) minmax(76px, auto)';

export type InterviewSortField =
  | 'meetingLink'
  | 'interviewType'
  | 'company'
  | 'role'
  | 'recruiter'
  | 'scheduledDate'
  | 'status';

/** Meeting link cell: bid-board–style single line + link coloring. */
export const interviewMeetingLinkFieldSx = {
  ...bidBoardTextFieldSingleLineEllipsisSx,
  '& .MuiOutlinedInput-input, & .MuiInputBase-input': {
    ...bidBoardTextFieldSingleLineEllipsisSx['& .MuiOutlinedInput-input, & .MuiInputBase-input'],
    color: 'primary.main',
    textDecoration: 'underline',
    textUnderlineOffset: '2px',
  },
} as const;
