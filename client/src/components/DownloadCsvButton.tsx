import { useState } from 'react';
import {
  Button,
  Menu,
  MenuItem,
  ListItemText,
  ListItemIcon,
  Tooltip,
} from '@mui/material';
import DownloadIcon from '@mui/icons-material/Download';
import api from '../api/client';

type Props = {
  groupId: string;
  kind: 'bids' | 'interviews';
  /** Label override (defaults to "Download" + kind). */
  label?: string;
  disabled?: boolean;
};

const RANGES: Array<{ key: 'daily' | 'weekly' | 'monthly'; label: string }> = [
  { key: 'daily', label: 'Today (UTC)' },
  { key: 'weekly', label: 'Last 7 days' },
  { key: 'monthly', label: 'This month (UTC)' },
];

/**
 * Streamed CSV download. Uses axios with `responseType: 'blob'` so the browser receives a binary
 * file even though the server emits text/csv. Server-side scope is enforced by role.
 */
export function DownloadCsvButton({ groupId, kind, label, disabled }: Props) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);

  async function download(range: 'daily' | 'weekly' | 'monthly') {
    setDownloading(range);
    setAnchor(null);
    try {
      const res = await api.get(`/groups/${groupId}/export`, {
        params: { kind, range },
        responseType: 'blob',
      });
      const cd = String(res.headers['content-disposition'] || '');
      const m = cd.match(/filename="([^"]+)"/);
      const filename = m?.[1] || `${kind}-${range}.csv`;
      const blob = new Blob([res.data], { type: 'text/csv;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (e) {
      console.error('CSV download failed', e);
    } finally {
      setDownloading(null);
    }
  }

  return (
    <>
      <Tooltip title={`Download ${kind} as CSV`}>
        <span>
          <Button
            size="small"
            variant="outlined"
            startIcon={<DownloadIcon />}
            disabled={disabled || downloading != null}
            onClick={(e) => setAnchor(e.currentTarget)}
          >
            {downloading ? 'Downloading…' : label || `Download ${kind}`}
          </Button>
        </span>
      </Tooltip>
      <Menu open={Boolean(anchor)} anchorEl={anchor} onClose={() => setAnchor(null)}>
        {RANGES.map((r) => (
          <MenuItem key={r.key} onClick={() => download(r.key)}>
            <ListItemIcon>
              <DownloadIcon fontSize="small" />
            </ListItemIcon>
            <ListItemText primary={r.label} />
          </MenuItem>
        ))}
      </Menu>
    </>
  );
}
