import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, getToken, setToken } from "../api/client";

interface AuthUser {
  userId: string;
  email: string;
  displayName: string;
}

interface AuthState {
  user: AuthUser | null;
  ready: boolean;
  login: (email: string, password: string) => Promise<void>;
  logout: () => void;
}

const AuthCtx = createContext<AuthState | null>(null);

const USER_KEY = "mtrx_user";

function readStoredUser(): AuthUser | null {
  const raw = localStorage.getItem(USER_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as AuthUser;
  } catch {
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    if (getToken()) {
      const stored = readStoredUser();
      if (stored) {
        setUser(stored);
      }
    }
    setReady(true);
  }, []);

  const value = useMemo<AuthState>(
    () => ({
      user,
      ready,
      login: async (email, password) => {
        const resp = await api.login(email, password);
        setToken(resp.accessToken);
        const u: AuthUser = { userId: resp.userId, email: resp.email, displayName: resp.displayName };
        localStorage.setItem(USER_KEY, JSON.stringify(u));
        setUser(u);
      },
      logout: () => {
        setToken(null);
        localStorage.removeItem(USER_KEY);
        setUser(null);
      },
    }),
    [user, ready],
  );

  return <AuthCtx.Provider value={value}>{children}</AuthCtx.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthCtx);
  if (!ctx) throw new Error("useAuth must be used inside AuthProvider");
  return ctx;
}
