import axios, { isAxiosError } from 'axios';

const API_URL = import.meta.env.VITE_API_URL || '';

const api = axios.create({
  baseURL: `${API_URL}/api`,
  /** Bid board / stats can be slow; avoid hanging forever on stalled connections. */
  timeout: Number(import.meta.env.VITE_API_TIMEOUT_MS) || 90_000,
});

function applyAuthToken(token: string | null) {
  if (token) {
    api.defaults.headers.common.Authorization = `Bearer ${token}`;
    localStorage.setItem('devstrider_token', token);
  } else {
    delete api.defaults.headers.common.Authorization;
    localStorage.removeItem('devstrider_token');
  }
}

api.interceptors.response.use(
  (r) => r,
  (err) => {
    if (!isAxiosError(err) || err.response?.status !== 401) {
      return Promise.reject(err);
    }
    const url = err.config?.url ?? '';
    if (url.includes('/auth/login') || url.includes('/auth/register')) {
      return Promise.reject(err);
    }
    applyAuthToken(null);
    if (typeof window !== 'undefined' && !window.location.pathname.startsWith('/login')) {
      window.location.replace('/login');
    }
    return Promise.reject(err);
  }
);

export function setAuthToken(token: string | null) {
  applyAuthToken(token);
}

export function loadStoredToken() {
  const t = localStorage.getItem('devstrider_token');
  if (t) applyAuthToken(t);
}

export default api;
