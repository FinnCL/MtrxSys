import { useMemo, useState, type ReactNode } from "react";
import { api, getToken, setToken } from "../api/client";
import { AuthCtx, type AuthState, type AuthUser } from "./useAuth";

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
  // Inicialização preguiçosa: lê o usuário salvo já na 1ª render. Sem useEffect → sem
  // setState dentro de efeito e sem o "Carregando" piscar (o token é síncrono).
  const [user, setUser] = useState<AuthUser | null>(() => (getToken() ? readStoredUser() : null));
  const [ready] = useState(true);

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
