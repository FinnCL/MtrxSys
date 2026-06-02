import { useMemo, useState, type ReactNode } from "react";
import { api, getToken, setToken } from "../api/client";
import { AuthCtx, type AuthState, type AuthUser } from "./useAuth";

const USER_KEY = "mtrx_user";
const LANDING_KEY = "mtrx_landing_url";

// Só aceita voltar pra uma origem localhost (evita open-redirect via fragment forjado).
const isSafeLandingUrl = (url: string | null): url is string =>
  !!url && /^https?:\/\/localhost(:\d+)?$/i.test(url);

function readStoredUser(): AuthUser | null {
  const raw = localStorage.getItem(USER_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as AuthUser;
  } catch {
    return null;
  }
}

// Quando o usuário entra via landing page de múltiplos ambientes (localhost:5175), a landing
// faz o login no backend dele e redireciona pra cá com o JWT + dados do usuário no fragment
// da URL (#token=...&user=...). Fragment escolhido em vez de query string porque NÃO vai pro
// servidor (sem risco de aparecer em log) e é limpo da URL logo após consumido.
function consumeAuthFromUrlHash(): AuthUser | null {
  if (typeof window === "undefined" || !window.location.hash) return null;
  const params = new URLSearchParams(window.location.hash.slice(1));
  const token = params.get("token");
  const userJson = params.get("user");
  if (!token || !userJson) return null;
  try {
    const u = JSON.parse(userJson) as AuthUser;
    setToken(token);
    localStorage.setItem(USER_KEY, JSON.stringify(u));
    // Guarda de onde o usuário veio (a landing), pra o logout voltar pro hub. sessionStorage:
    // por aba, sobrevive a F5, e some quando a aba fecha (acesso direto = sem isto = login local).
    const landing = params.get("landing");
    if (isSafeLandingUrl(landing)) {
      sessionStorage.setItem(LANDING_KEY, landing);
    }
    // Tira o hash da URL pra não vazar o JWT no histórico do navegador nem em links copiados.
    window.history.replaceState(null, "", window.location.pathname + window.location.search);
    return u;
  } catch {
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  // Inicialização preguiçosa: lê o usuário salvo já na 1ª render. Sem useEffect → sem
  // setState dentro de efeito e sem o "Carregando" piscar (o token é síncrono). Tenta
  // primeiro consumir um login vindo da landing (URL hash); senão, lê do localStorage.
  const [user, setUser] = useState<AuthUser | null>(() => {
    const fromHash = consumeAuthFromUrlHash();
    if (fromHash) return fromHash;
    return getToken() ? readStoredUser() : null;
  });
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
        // Veio da landing multi-ambiente? Volta pro hub (os 10 cards) em vez do login isolado
        // deste ambiente. Acesso direto (sem landing guardada) → login local, como antes.
        const landing = sessionStorage.getItem(LANDING_KEY);
        if (isSafeLandingUrl(landing)) {
          window.location.href = landing;
          return;
        }
        setUser(null);
      },
    }),
    [user, ready],
  );

  return <AuthCtx.Provider value={value}>{children}</AuthCtx.Provider>;
}
