import {
  Box,
  Dialog,
  DialogContent,
  DialogTitle,
  IconButton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';

type Props = {
  open: boolean;
  onClose: () => void;
  title: string;
  /** Optional subtitle line, e.g. "Acme Co · Senior Engineer". */
  subtitle?: string;
  /** Pre-formatted text body — newlines preserved, monospace+serif at the caller's choice. */
  body: string;
  /** Copy-button aria/tooltip label, e.g. "job description". */
  copyLabel: string;
  /** Use serif body for resume "real-look"; sans-serif otherwise. */
  serif?: boolean;
  /**
   * Verbatim lines (as they appear in `body`) that should render bold + slightly larger for
   * emphasis — used by the resume composer to highlight experience-header lines. Copy still
   * yields plain `body`, so paste targets see the same text without any markup.
   */
  boldLines?: string[];
};

async function copyToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    /* ignore */
  }
}

/**
 * Modal viewer for long-form bid content (JD or composed resume). Replaces the previous hover-
 * tooltip preview with a click-to-open dialog that preserves whitespace, scrolls cleanly, and
 * offers a one-click copy.
 */
export function BidContentViewer({
  open,
  onClose,
  title,
  subtitle,
  body,
  copyLabel,
  serif,
  boldLines,
}: Props) {
  const trimmed = (body || '').trim();
  const boldSet = boldLines && boldLines.length > 0 ? new Set(boldLines) : null;
  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth scroll="paper">
      <DialogTitle sx={{ pb: 1.5 }}>
        <Stack
          direction="row"
          alignItems="flex-start"
          justifyContent="space-between"
          spacing={1}
        >
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="h6" sx={{ lineHeight: 1.25 }}>
              {title}
            </Typography>
            {subtitle ? (
              <Typography variant="caption" color="text.secondary">
                {subtitle}
              </Typography>
            ) : null}
          </Box>
          <Stack direction="row" spacing={0.25} sx={{ flexShrink: 0 }}>
            <Tooltip title={trimmed ? `Copy ${copyLabel}` : 'Nothing to copy'}>
              <span>
                <IconButton
                  size="small"
                  aria-label={`Copy ${copyLabel}`}
                  onClick={() => void copyToClipboard(body)}
                  disabled={!trimmed}
                >
                  <ContentCopyIcon fontSize="small" />
                </IconButton>
              </span>
            </Tooltip>
            <Tooltip title="Close">
              <IconButton size="small" aria-label="Close" onClick={onClose}>
                <CloseIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          </Stack>
        </Stack>
      </DialogTitle>
      <DialogContent dividers>
        {trimmed ? (
          <Box
            sx={{
              whiteSpace: 'pre-wrap',
              wordBreak: 'break-word',
              fontFamily: serif
                ? '"Georgia", "Times New Roman", serif'
                : '"Inter", "Helvetica", "Arial", sans-serif',
              fontSize: '0.95rem',
              lineHeight: 1.55,
              color: 'text.primary',
            }}
          >
            {boldSet
              ? body.split('\n').map((line, idx, all) => {
                  const isBold = boldSet.has(line);
                  return (
                    <Box
                      key={idx}
                      component="span"
                      sx={
                        isBold
                          ? {
                              display: 'block',
                              fontWeight: 700,
                              fontSize: '1.05rem',
                              mt: 0.5,
                            }
                          : { display: 'block' }
                      }
                    >
                      {/** Preserve blank lines so spacing matches the underlying text. */}
                      {line || (idx < all.length - 1 ? ' ' : '')}
                    </Box>
                  );
                })
              : body}
          </Box>
        ) : (
          <Typography variant="body2" color="text.secondary">
            Nothing to show yet.
          </Typography>
        )}
      </DialogContent>
    </Dialog>
  );
}
