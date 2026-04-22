import { Box, IconButton, Stack, Tooltip, Typography } from '@mui/material';
import ArrowDownwardIcon from '@mui/icons-material/ArrowDownward';
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward';
import { bidBoardCellSx, bidBoardRowGridSx } from '../bid/bidBoardGrid';
import { INTERVIEW_GRID_COLS, type InterviewSortField } from './interviewGrid';

type Col = {
  label: string;
  sort?: InterviewSortField;
};

const COLS: Col[] = [
  { label: 'Link', sort: 'meetingLink' },
  { label: 'Type', sort: 'interviewType' },
  { label: 'Company', sort: 'company' },
  { label: 'Role', sort: 'role' },
  { label: 'Recruiter', sort: 'recruiter' },
  { label: 'Attendees' },
  { label: 'Date', sort: 'scheduledDate' },
  { label: 'Time' },
  { label: 'Dur' },
  { label: 'Status', sort: 'status' },
  { label: 'Comment' },
  { label: 'Actions' },
];

type Props = {
  sortField: InterviewSortField;
  sortDir: 'asc' | 'desc';
  onSort: (field: InterviewSortField) => void;
};

function SortBtn({
  active,
  dir,
  onClick,
  title,
}: {
  active: boolean;
  dir: 'asc' | 'desc';
  onClick: () => void;
  title: string;
}) {
  return (
    <Tooltip title={title}>
      <IconButton size="small" onClick={onClick} sx={{ p: 0.25 }} aria-label={title}>
        {active ? (
          dir === 'asc' ? (
            <ArrowUpwardIcon sx={{ fontSize: 16 }} />
          ) : (
            <ArrowDownwardIcon sx={{ fontSize: 16 }} />
          )
        ) : (
          <ArrowDownwardIcon sx={{ fontSize: 16, opacity: 0.22 }} />
        )}
      </IconButton>
    </Tooltip>
  );
}

export function InterviewStickyHeader({ sortField, sortDir, onSort }: Props) {
  return (
    <Box
      sx={{
        position: 'sticky',
        top: 0,
        zIndex: 4,
        bgcolor: 'background.paper',
        borderBottom: 1,
        borderColor: 'divider',
      }}
    >
      <Box
        sx={{
          ...bidBoardRowGridSx,
          gridTemplateColumns: INTERVIEW_GRID_COLS,
        }}
      >
        {COLS.map((c, colIdx) => (
          <Box key={`${c.label}-${colIdx}`} sx={bidBoardCellSx}>
            <Stack
              direction="row"
              spacing={0.25}
              alignItems="center"
              justifyContent="center"
              useFlexGap
              sx={{ width: '100%', minWidth: 0 }}
            >
              <Typography
                variant="caption"
                color="text.secondary"
                fontWeight={600}
                noWrap
                sx={{ maxWidth: c.sort ? 'calc(100% - 28px)' : '100%' }}
              >
                {c.label}
              </Typography>
              {c.sort && (
                <SortBtn
                  active={sortField === c.sort}
                  dir={sortDir}
                  onClick={() => onSort(c.sort!)}
                  title={`Sort by ${c.label}`}
                />
              )}
            </Stack>
          </Box>
        ))}
      </Box>
    </Box>
  );
}
