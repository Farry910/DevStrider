import type { TooltipProps } from '@mui/material/Tooltip';

/**
 * Fixed-size tooltip shell with scroll — keeps long JD / GPT / URLs from covering the viewport.
 * Use with MUI Tooltip `slotProps={BID_BOARD_TOOLTIP_SLOT_PROPS}` (and `disableInteractive={false}` so users can scroll).
 */
export const BID_BOARD_TOOLTIP_SLOT_PROPS = {
  tooltip: {
    sx: {
      maxHeight: 240,
      maxWidth: 420,
      overflowY: 'auto',
      overflowX: 'hidden',
      boxSizing: 'border-box',
      py: 1,
      px: 1.25,
    },
  },
} satisfies NonNullable<TooltipProps['slotProps']>;

export const BID_BOARD_TOOLTIP_COMMON = {
  slotProps: BID_BOARD_TOOLTIP_SLOT_PROPS,
  disableInteractive: false,
} satisfies Partial<TooltipProps>;
