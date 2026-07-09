import { useState, type FormEvent } from "react";
import { useAuth } from "../auth/useAuth";
import { ApiError } from "../api/client";

function EyeIcon({ hidden }: { hidden: boolean }) {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8S1 12 1 12z" />
      <circle cx="12" cy="12" r="3" />
      {hidden && <line x1="3" y1="3" x2="21" y2="21" />}
    </svg>
  );
}

export function LoginScreen() {
  const { login } = useAuth();
  // Homolog (VITE_APP_LABEL="HML") abre com os campos VAZIOS — segurança (pedido do operador). Nos
  // demais ambientes (dev + os 10 de prod) mantém o pré-preenchimento de conveniência; o login real
  // de prod é pela landing (que já autentica por-chip), então o form direto é atalho de dev/admin.
  const isHomolog = import.meta.env.VITE_APP_LABEL === "HML";
  const [email, setEmail] = useState(isHomolog ? "" : "admin@local");
  const [password, setPassword] = useState(isHomolog ? "" : "admin123!");
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await login(email, password);
    } catch (ex) {
      if (ex instanceof ApiError) {
        setError(ex.status === 401 ? "Credenciais inválidas" : ex.message);
      } else {
        setError("Erro de conexão");
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login-shell">
      <form className="login-card" onSubmit={onSubmit}>
        <h1>MtrxSys</h1>
        <label>
          <span>Email</span>
          <input value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" />
        </label>
        <label>
          <span>Senha</span>
          <div className="password-field">
            <input
              type={showPassword ? "text" : "password"}
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
            />
            <button
              type="button"
              className="password-toggle"
              onClick={() => setShowPassword((v) => !v)}
              aria-label={showPassword ? "Ocultar senha" : "Mostrar senha"}
              tabIndex={-1}
            >
              <EyeIcon hidden={showPassword} />
            </button>
          </div>
        </label>
        {error && <p className="login-error">{error}</p>}
        <button type="submit" disabled={busy}>
          {busy ? "Entrando..." : "Entrar"}
        </button>
      </form>
    </div>
  );
}
