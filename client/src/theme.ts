import { createTheme } from '@mui/material/styles';

export const appTheme = createTheme({
  palette: {
    mode: 'dark',
    primary: { main: '#7dd3fc' },
    secondary: { main: '#c4b5fd' },
    background: {
      default: '#0b0f14',
      paper: '#121922',
    },
    divider: 'rgba(255,255,255,0.08)',
  },
  shape: { borderRadius: 10 },
  typography: {
    fontFamily: '"DM Sans", "Roboto", "Helvetica", "Arial", sans-serif',
    h5: { fontWeight: 700 },
    h6: { fontWeight: 600 },
  },
  components: {
    MuiButton: { defaultProps: { variant: 'contained', disableElevation: true } },
    MuiTextField: { defaultProps: { size: 'small', variant: 'outlined' } },
  },
});
