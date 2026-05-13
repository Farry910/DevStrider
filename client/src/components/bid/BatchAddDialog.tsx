import { useMemo, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import WarningAmberIcon from '@mui/icons-material/WarningAmber';
import CheckCircleIcon from '@mui/icons-material/CheckCircle';
import { parseBatchInput } from '../../utils/parseFastFeed';

type Props = {
  open: boolean;
  onClose: () => void;
};

const PLACEHOLDER = `https://example.com/jobs/123\ta7f3k-Example Inc-Software Engineer-Node.js-React
https://example.com/jobs/456\tb2c4d-Acme Co-Lead Engineer - Backend-Go-Postgres
https://example.com/jobs/789`;

export function BatchAddDialog({ open, onClose }: Props) {
  const [raw, setRaw] = useState('');
  const rows = useMemo(() => parseBatchInput(raw), [raw]);
  const validCount = rows.filter((r) => r.valid).length;
  const invalidCount = rows.length - validCount;

  return (
    <Dialog open={open} onClose={onClose} maxWidth="lg" fullWidth>
      <DialogTitle>Batch add bids</DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Alert severity="info" variant="outlined">
            <Typography variant="caption" component="div">
              One row per line in the form <code>URL&lt;TAB&gt;resumeId-company-role-stack1-stack2…</code>.
            </Typography>
            <Typography variant="caption" component="div" sx={{ mt: 0.5 }}>
              The fast-feed part is optional. Fields are dash-separated; spaced dashes (<code>{' - '}</code>)
              inside a field are kept as text. Hyphenated compounds like <code>Full-Stack</code> or{' '}
              <code>CI-CD</code> will be split — fix them in the preview.
            </Typography>
          </Alert>

          <TextField
            value={raw}
            onChange={(e) => setRaw(e.target.value)}
            multiline
            minRows={6}
            maxRows={14}
            fullWidth
            placeholder={PLACEHOLDER}
            inputProps={{ 'aria-label': 'Batch input', spellCheck: 'false' }}
            sx={{ '& textarea': { fontFamily: 'monospace', fontSize: '0.8125rem' } }}
          />

          <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
            <Chip
              size="small"
              color={validCount > 0 ? 'success' : 'default'}
              variant={validCount > 0 ? 'filled' : 'outlined'}
              icon={<CheckCircleIcon />}
              label={`${validCount} valid`}
            />
            <Chip
              size="small"
              color={invalidCount > 0 ? 'warning' : 'default'}
              variant={invalidCount > 0 ? 'filled' : 'outlined'}
              icon={invalidCount > 0 ? <WarningAmberIcon /> : undefined}
              label={`${invalidCount} need fix`}
            />
            <Typography variant="caption" color="text.secondary">
              {rows.length} parsed row{rows.length === 1 ? '' : 's'}
            </Typography>
          </Stack>

          {rows.length > 0 && (
            <Box sx={{ overflowX: 'auto' }}>
              <Table size="small" stickyHeader>
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ width: 36 }}>#</TableCell>
                    <TableCell>URL</TableCell>
                    <TableCell sx={{ width: 96 }}>Resume ID</TableCell>
                    <TableCell sx={{ width: 140 }}>Company</TableCell>
                    <TableCell sx={{ width: 200 }}>Role</TableCell>
                    <TableCell>Stacks</TableCell>
                    <TableCell sx={{ width: 56 }}>Status</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {rows.map((r) => (
                    <TableRow
                      key={r.index}
                      sx={{
                        bgcolor: r.valid ? 'transparent' : 'rgba(255,193,7,0.08)',
                      }}
                    >
                      <TableCell>
                        <Typography variant="caption" color="text.secondary">
                          {r.index}
                        </Typography>
                      </TableCell>
                      <TableCell sx={{ wordBreak: 'break-all', maxWidth: 220 }}>
                        {r.url ? (
                          <Typography variant="caption" sx={{ fontFamily: 'monospace' }}>
                            {r.url.replace(/^https?:\/\//i, '')}
                          </Typography>
                        ) : (
                          <Typography variant="caption" color="warning.main">
                            (missing)
                          </Typography>
                        )}
                      </TableCell>
                      <TableCell>{r.fastFeed?.resumeId ?? '—'}</TableCell>
                      <TableCell>{r.fastFeed?.company ?? '—'}</TableCell>
                      <TableCell>{r.fastFeed?.role ?? '—'}</TableCell>
                      <TableCell>
                        {r.fastFeed?.primaryStacks.length
                          ? r.fastFeed.primaryStacks.join(', ')
                          : '—'}
                      </TableCell>
                      <TableCell>
                        {r.valid ? (
                          <Tooltip title="Parsed OK">
                            <CheckCircleIcon fontSize="small" color="success" />
                          </Tooltip>
                        ) : (
                          <Tooltip title={r.warnings.join(' · ') || 'Invalid'}>
                            <WarningAmberIcon fontSize="small" color="warning" />
                          </Tooltip>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Box>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Close</Button>
      </DialogActions>
    </Dialog>
  );
}
