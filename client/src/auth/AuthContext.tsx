import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import api, { loadStoredToken, setAuthToken } from '../api/client';
import { disconnectAppSocket, syncAppSocketAuth } from '../socket/appSocket';

type User = {
  id: string;
  email: string;
  nickname: string;
  avatarId: string;
  /** 'admin' = seeded platform admin (approves groups, transfers ownership). */
  platformRole: 'user' | 'admin';
};

function normalizeUser(u: {
  id: string;
  email: string;
  nickname: string;
  avatarId?: string;
  platformRole?: string;
}): User {
  return {
    id: u.id,
    email: u.email,
    nickname: u.nickname,
    avatarId: u.avatarId ?? 'initial',
    platformRole: u.platformRole === 'admin' ? 'admin' : 'user',
  };
}

type AuthState = {
  user: User | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, nickname: string, password: string) => Promise<void>;
  logout: () => void;
  applyUser: (user: User) => void;
};

const Ctx = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    loadStoredToken();
    api
      .get('/auth/me')
      .then((r) => {
        setUser(normalizeUser(r.data.user));
        syncAppSocketAuth();
      })
      .catch(() => {
        setUser(null);
        disconnectAppSocket();
      })
      .finally(() => setLoading(false));
  }, []);

  const login = useCallback(async (email: string, password: string) => {
    const { data } = await api.post('/auth/login', { email, password });
    setAuthToken(data.token);
    setUser(normalizeUser(data.user));
    syncAppSocketAuth();
  }, []);

  const register = useCallback(async (email: string, nickname: string, password: string) => {
    const { data } = await api.post('/auth/register', { email, nickname, password });
    setAuthToken(data.token);
    setUser(normalizeUser(data.user));
    syncAppSocketAuth();
  }, []);

  const logout = useCallback(() => {
    setAuthToken(null);
    setUser(null);
    disconnectAppSocket();
  }, []);

  const applyUser = useCallback((u: User) => {
    setUser(normalizeUser(u));
  }, []);

  const value = useMemo(
    () => ({ user, loading, login, register, logout, applyUser }),
    [user, loading, login, register, logout, applyUser]
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useAuth() {
  const v = useContext(Ctx);
  if (!v) throw new Error('useAuth outside AuthProvider');
  return v;
}
