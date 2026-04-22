import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Alert, Box, Button, Stack, Typography } from '@mui/material';

type Props = { children: ReactNode };

type State = { error: Error | null };

export class AppErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('UI error boundary:', error, info.componentStack);
  }

  render() {
    if (this.state.error) {
      return (
        <Box sx={{ p: 3, maxWidth: 560, mx: 'auto', mt: 4 }}>
          <Alert severity="error" sx={{ mb: 2 }}>
            Something went wrong in this view.
          </Alert>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2, fontFamily: 'monospace' }}>
            {this.state.error.message}
          </Typography>
          <Stack direction="row" spacing={1}>
            <Button variant="contained" onClick={() => this.setState({ error: null })}>
              Try again
            </Button>
            <Button
              variant="outlined"
              onClick={() => {
                window.location.assign('/');
              }}
            >
              Go home
            </Button>
          </Stack>
        </Box>
      );
    }
    return this.props.children;
  }
}
