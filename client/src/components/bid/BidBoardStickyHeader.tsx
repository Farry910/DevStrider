import type { ReactNode } from 'react';
import { Box, IconButton, Stack, Tooltip, Typography } from '@mui/material';
import ArrowDownwardIcon from '@mui/icons-material/ArrowDownward';
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward';
import { bidBoardRowGridSx, bidBoardStickyActionsSx, type BidSortField } from './bidBoardGrid';

/** Stable IDs for column-scoped filters; less brittle than relying on label strings. */
type FilterId = 'link' | 'role';

type Col = {
  label: string;
  sort?: BidSortField;
  filterId?: FilterId;
};

const COLS: Col[] = [
  { label: 'Link', sort: 'linkCreatedAt', filterId: 'link' },
  { label: 'Resume ID', sort: 'resumeId' },
  { label: 'Company', sort: 'company' },
  { label: 'Role', sort: 'role', filterId: 'role' },
  { label: 'Stacks' },
  { label: 'Status', sort: 'status' },
  { label: 'Origin', sort: 'origin' },
  { label: 'Bidders' },
  { label: 'JD', sort: 'jobDescription' },
  { label: 'GPT res.' },
  { label: 'Comment' },
  { label: 'Created / edit', sort: 'bidUpdatedAt' },
  { label: 'Actions' },
];

type Props = {
  sortField: BidSortField;
  sortDir: 'asc' | 'desc';
  onSort: (field: BidSortField) => void;
  /** Optional inline filter inputs to render under specific column headers. */
  filterByColumn?: Partial<Record<FilterId, ReactNode>>;
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

export function BidBoardStickyHeader({ sortField, sortDir, onSort, filterByColumn }: Props) {
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
      <Box sx={bidBoardRowGridSx}>
        {COLS.map((c, colIdx) => {
          const filterNode = c.filterId ? filterByColumn?.[c.filterId] : undefined;
          return (
            <Box
              key={`${c.label}-${colIdx}`}
              sx={{
                width: '100%',
                minWidth: 0,
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'stretch',
                gap: 0.25,
                ...(colIdx === COLS.length - 1 ? bidBoardStickyActionsSx : {}),
              }}
            >
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
              {filterNode && (
                /** Filter cell sized to its column; horizontal scroll on overflow keeps the grid aligned. */
                <Box sx={{ width: '100%', minWidth: 0, px: 0.25, pb: 0.5 }}>{filterNode}</Box>
              )}
            </Box>
          );
        })}
      </Box>
    </Box>
  );
}
