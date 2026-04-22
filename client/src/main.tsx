import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { CssBaseline } from '@mui/material';
import { ThemeProvider } from '@mui/material/styles';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import App from './App';
import { appTheme } from './theme';
import { AuthProvider } from './auth/AuthContext';
import { AppErrorBoundary } from './components/AppErrorBoundary';

function shouldRetryQuery(failureCount: number, error: unknown): boolean {
  if (failureCount >= 2) return false;
  if (!isAxiosError(error)) return failureCount < 1;
  const st = error.response?.status;
  if (st === 401 || st === 403 || st === 404) return false;
  if (st != null && st >= 400 && st < 500 && st !== 408 && st !== 429) return false;
  return true;
}

const qc = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      gcTime: 5 * 60_000,
      retry: shouldRetryQuery,
      refetchOnWindowFocus: true,
      networkMode: 'online',
    },
    mutations: {
      retry: 0,
    },
  },
});

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={qc}>
      <ThemeProvider theme={appTheme}>
        <CssBaseline />
        <BrowserRouter>
          <AuthProvider>
            <AppErrorBoundary>
              <App />
            </AppErrorBoundary>
          </AuthProvider>
        </BrowserRouter>
      </ThemeProvider>
    </QueryClientProvider>
  </React.StrictMode>
);
