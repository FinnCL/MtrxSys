using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OtpNet;
using QRCoder;

// ─────────────────────────────────────────────────────────────────────────────
// MtrxSys.Gate — portão de autenticação (1 credencial FIXA: usuário + senha + 2FA).
//   • Credencial vem 100% da config (GATE_USER / GATE_PASSWORD_HASH / GATE_TOTP_SECRET).
//     NÃO há setup no 1º acesso → nenhuma janela aberta; só a tela de login.
//   • Sessão por cookie assinado no domínio inteiro → SSO em todos os subdomínios.
//   • Sessão ÚNICA: um novo login invalida a anterior (bloqueia 2 acessos).
//   • O Caddy chama /authz (forward_auth); sem sessão → 302 pra tela de login.
//   • Chaves de assinatura + sessão ativa ficam no volume /data (sobrevive a reinício).
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

var dataDir = Environment.GetEnvironmentVariable("GATE_DATA_DIR") ?? "/data";
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "dpkeys"));

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "dpkeys")))
    .SetApplicationName("mtrx-gate");

var app = builder.Build();

var USER        = (Environment.GetEnvironmentVariable("GATE_USER") ?? "admin").Trim();
var PW_HASH     = Environment.GetEnvironmentVariable("GATE_PASSWORD_HASH") ?? "";  // formato: iters.b64salt.b64hash
var TOTP_SECRET = Environment.GetEnvironmentVariable("GATE_TOTP_SECRET") ?? "";    // base32
var COOKIE_DOM  = Environment.GetEnvironmentVariable("GATE_COOKIE_DOMAIN") ?? "";  // ex.: .mtrxsys.com
var BASE_DOM    = Environment.GetEnvironmentVariable("GATE_BASE_DOMAIN") ?? "";    // ex.: mtrxsys.com
var PUBLIC_URL  = (Environment.GetEnvironmentVariable("GATE_PUBLIC_URL") ?? "").TrimEnd('/');
var HOME_URL    = (Environment.GetEnvironmentVariable("GATE_HOME_URL") ?? "").TrimEnd('/');
const string COOKIE = "mtrx_gate";

var sessions  = new SessionStore(Path.Combine(dataDir, "session.json"));
var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("mtrx-gate.session.v1");
var throttle  = new Throttle(maxFails: 5, window: TimeSpan.FromMinutes(1), lockFor: TimeSpan.FromMinutes(1));

bool IsAuthed(HttpRequest req)
{
    var c = req.Cookies[COOKIE];
    if (string.IsNullOrEmpty(c)) return false;
    try
    {
        var sid = protector.Unprotect(c);
        var active = sessions.Active;
        return !string.IsNullOrEmpty(active)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sid), Encoding.UTF8.GetBytes(active));
    }
    catch { return false; }
}

void IssueSession(HttpResponse res)
{
    var sid = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    sessions.Active = sid;                    // substitui a sessão anterior → só 1 acesso ativo
    res.Cookies.Append(COOKIE, protector.Protect(sid), new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Domain = string.IsNullOrEmpty(COOKIE_DOM) ? null : COOKIE_DOM,
        Expires = DateTimeOffset.UtcNow.AddDays(30),   // persistente = "fica salvo"
        Path = "/",
    });
}

string SafeRd(string? rd)
{
    var fallback = string.IsNullOrEmpty(HOME_URL) ? "/" : HOME_URL;
    if (string.IsNullOrWhiteSpace(rd)) return fallback;
    if (!Uri.TryCreate(rd, UriKind.Absolute, out var u)) return fallback;
    if (u.Scheme != "https") return fallback;
    if (!string.IsNullOrEmpty(BASE_DOM)
        && !(u.Host.Equals(BASE_DOM, StringComparison.OrdinalIgnoreCase)
             || u.Host.EndsWith("." + BASE_DOM, StringComparison.OrdinalIgnoreCase)))
        return fallback;
    return rd;
}

static bool FixedEq(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

// ── forward_auth: o Caddy pergunta se a requisição pode passar ────────────────
app.MapMethods("/authz", ["GET", "HEAD"], (HttpRequest req) =>
{
    if (IsAuthed(req)) return Results.NoContent();          // 204 → Caddy libera
    var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? "https";
    var host  = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? "";
    var uri   = req.Headers["X-Forwarded-Uri"].FirstOrDefault() ?? "/";
    var rd    = $"{proto}://{host}{uri}";
    return Results.Redirect($"{PUBLIC_URL}/?rd={Uri.EscapeDataString(rd)}");
});

app.MapGet("/healthz", () => Results.Text("ok"));

// ── Configurar/recuperar 2FA — pede a senha (a tranca), depois mostra o QR ──
app.MapGet("/enroll", () => Results.Content(EnrollAskPage(null), "text/html; charset=utf-8"));

app.MapPost("/enroll", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    if (throttle.Locked(out var secs))
        return Results.Content(EnrollAskPage($"Muitas tentativas. Tente de novo em {secs}s."), "text/html; charset=utf-8");
    if (!VerifyPassword(PW_HASH, form["password"].ToString()))
    {
        throttle.Fail();
        return Results.Content(EnrollAskPage("Senha incorreta."), "text/html; charset=utf-8");
    }
    throttle.Reset();
    if (string.IsNullOrEmpty(TOTP_SECRET))
        return Results.Content(EnrollAskPage("2FA não configurado no servidor."), "text/html; charset=utf-8");
    var otpauth = $"otpauth://totp/MtrxSys:{Uri.EscapeDataString(USER)}?secret={TOTP_SECRET}&issuer=MtrxSys&digits=6&period=30";
    using var qr = new QRCodeGenerator();
    var png = new PngByteQRCode(qr.CreateQrCode(otpauth, QRCodeGenerator.ECCLevel.Q)).GetGraphic(6);
    var img = "data:image/png;base64," + Convert.ToBase64String(png);
    return Results.Content(EnrollQrPage(img, TOTP_SECRET), "text/html; charset=utf-8");
});

app.MapGet("/", (HttpRequest req) =>
{
    var rd = req.Query["rd"].FirstOrDefault();
    if (IsAuthed(req)) return Results.Redirect(SafeRd(rd));
    return Results.Content(LoginPage(rd, null), "text/html; charset=utf-8");
});

// ── LOGIN: usuário + senha + 2FA (credencial fixa da config) ──────────────────
app.MapPost("/login", async (HttpRequest req, HttpResponse res) =>
{
    var form = await req.ReadFormAsync();
    var rd = form["rd"].ToString();

    if (throttle.Locked(out var secs))
        return Results.Content(LoginPage(rd, $"Muitas tentativas. Tente de novo em {secs}s."), "text/html; charset=utf-8");

    var u    = form["user"].ToString().Trim();
    var pass = form["password"].ToString();
    var code = form["code"].ToString().Trim();

    // '&' (não '&&') pra avaliar os 3 sempre — não vaza qual fator falhou pelo tempo.
    var ok = FixedEq(u, USER) & VerifyPassword(PW_HASH, pass) & VerifyTotp(TOTP_SECRET, code);
    if (!ok)
    {
        throttle.Fail();
        return Results.Content(LoginPage(rd, "Usuário, senha ou código 2FA inválido."), "text/html; charset=utf-8");
    }
    throttle.Reset();
    IssueSession(res);                        // invalida qualquer sessão anterior
    return Results.Redirect(SafeRd(rd));
});

app.MapPost("/logout", (HttpResponse res) =>
{
    sessions.Active = null;
    res.Cookies.Delete(COOKIE, new CookieOptions { Domain = string.IsNullOrEmpty(COOKIE_DOM) ? null : COOKIE_DOM, Path = "/" });
    return Results.Redirect($"{PUBLIC_URL}/");
});

// Verifica a senha contra o hash da config no formato "iters.b64salt.b64hash".
static bool VerifyPassword(string stored, string pass)
{
    if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(pass)) return false;
    var parts = stored.Split('.');
    if (parts.Length != 3 || !int.TryParse(parts[0], out var iters)) return false;
    try
    {
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(pass, salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
    catch { return false; }
}

static bool VerifyTotp(string secret, string code)
{
    if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
    try
    {
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
    }
    catch { return false; }
}

app.Run();

// ─────────────────────────── HTML ────────────────────────────────────────────
static string Shell(string title, string body) => $$"""
<!doctype html><html lang="pt-br"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{Enc(title)}}</title>
<link rel="icon" href="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAzMiAzMiI+PHJlY3QgeD0iMS41IiB5PSIxLjUiIHdpZHRoPSIyOSIgaGVpZ2h0PSIyOSIgcng9IjYiIGZpbGw9Im5vbmUiIHN0cm9rZT0iIzAwZmY0MSIgc3Ryb2tlLXdpZHRoPSIyIi8+PHRleHQgeD0iMTYiIHk9IjIyIiBmb250LXNpemU9IjE3IiBmb250LWZhbWlseT0ibW9ub3NwYWNlIiBmb250LXdlaWdodD0iNzAwIiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmaWxsPSIjMDBmZjQxIj5NPC90ZXh0Pjwvc3ZnPg==">
<style>
  :root { --bg:#0d1117; --card:#161b22; --border:#30363d; --fg:#e6edf3; --muted:#8b949e; --accent:#2f81f7; --danger:#f85149; }
  * { box-sizing:border-box; }
  body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--fg); font-family:system-ui,Segoe UI,Roboto,sans-serif; padding:16px; }
  .card { width:100%; max-width:340px; background:var(--card); border:1px solid var(--border); border-radius:12px; padding:18px 20px; }
  h1 { font-size:20px; margin:0 0 6px; text-align:center; }
  .brand { color:var(--accent); }
  p.sub { color:var(--muted); font-size:12px; margin:0 0 12px; text-align:center; }
  label { display:flex; flex-direction:column; gap:4px; font-size:12px; color:var(--muted); margin-bottom:8px; }
  input { background:#0d1117; border:1px solid var(--border); border-radius:8px; padding:10px 12px; color:var(--fg); font-size:16px; }
  input:focus { outline:none; border-color:var(--accent); }
  button { width:100%; background:var(--accent); color:#fff; border:0; border-radius:8px; padding:10px; font-size:15px; font-weight:600; cursor:pointer; margin-top:4px; }
  button:hover { filter:brightness(1.1); }
  .err { color:var(--danger); font-size:12px; margin:0 0 12px; text-align:center; }
  .foot { text-align:center; margin:16px 0 0; font-size:12px; }
  .foot a { color:var(--muted); text-decoration:none; }
  .foot a:hover { color:var(--accent); }
  .qr { display:block; margin:0 auto 12px; width:200px; height:200px; border-radius:8px; background:#fff; }
  .key { font-family:ui-monospace,monospace; font-size:12px; letter-spacing:0.4px; text-align:center; word-break:break-all; color:var(--fg); background:#0d1117; border:1px solid var(--border); border-radius:8px; padding:9px 10px; }
  .keynote { text-align:center; font-size:11px; color:var(--muted); margin:0 0 5px; }
  .pwd { position:relative; display:flex; }
  .pwd input { flex:1; padding-right:40px; }
  .eye { position:absolute; right:4px; top:50%; transform:translateY(-50%); width:34px; height:34px; margin:0; padding:0; background:transparent; border:0; color:var(--muted); cursor:pointer; display:flex; align-items:center; justify-content:center; }
  .eye:hover { color:var(--fg); filter:none; }
  .eye svg { width:18px; height:18px; }
  input.code { text-align:center; letter-spacing:0.35em; text-indent:0.35em; }
  input:-webkit-autofill, input:-webkit-autofill:hover, input:-webkit-autofill:focus {
    -webkit-box-shadow:0 0 0 1000px #0d1117 inset; -webkit-text-fill-color:var(--fg); caret-color:var(--fg);
  }
  @media (max-width: 380px) {
    .card { padding: 20px 18px; }
    .qr { width: 168px; height: 168px; }
    h1 { font-size: 19px; }
  }
</style></head><body><div class="card">{{body}}</div>
<script>
var EYE='<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z"/><circle cx="12" cy="12" r="3"/></svg>';
var EYEOFF='<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17.9 17.9A10.6 10.6 0 0 1 12 19c-7 0-11-7-11-7a18.5 18.5 0 0 1 5.1-5.9M9.9 4.2A9 9 0 0 1 12 4c7 0 11 7 11 7a18.6 18.6 0 0 1-2.2 3.2M1 1l22 22"/></svg>';
function tog(b){var i=b.parentNode.querySelector('input');var s=i.type==='password';i.type=s?'text':'password';b.innerHTML=s?EYEOFF:EYE;b.setAttribute('aria-label',s?'Ocultar senha':'Mostrar senha');}
</script>
</body></html>
""";

string LoginPage(string? rd, string? err) => Shell("Entrar — MtrxSys", $$"""
<h1><span class="brand">Mtrx</span>Sys</h1>
<p class="sub">Acesso restrito</p>
{{(err is null ? "" : $"<p class=\"err\">{Enc(err)}</p>")}}
<form method="post" action="/login" autocomplete="off">
  <input type="hidden" name="rd" value="{{Enc(rd ?? "")}}">
  <label>Usuário<input name="user" required autofocus></label>
  <label>Senha<span class="pwd"><input name="password" type="password" required><button type="button" class="eye" onclick="tog(this)" aria-label="Mostrar senha"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z"/><circle cx="12" cy="12" r="3"/></svg></button></span></label>
  <label>Código 2FA<input class="code" name="code" inputmode="numeric" pattern="[0-9]*" maxlength="6" placeholder="000000" required></label>
  <button type="submit">Entrar</button>
</form>
<p class="foot"><a href="/enroll">Configurar 2FA</a></p>
""");

string EnrollAskPage(string? err) => Shell("Configurar 2FA — MtrxSys", $$"""
<h1><span class="brand">Mtrx</span>Sys</h1>
<p class="sub">Configurar 2FA</p>
{{(err is null ? "" : $"<p class=\"err\">{Enc(err)}</p>")}}
<form method="post" action="/enroll" autocomplete="off">
  <label>Senha<span class="pwd"><input name="password" type="password" required autofocus><button type="button" class="eye" onclick="tog(this)" aria-label="Mostrar senha"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z"/><circle cx="12" cy="12" r="3"/></svg></button></span></label>
  <button type="submit">Ver QR</button>
</form>
<p class="foot"><a href="/">Voltar ao login</a></p>
""");

string EnrollQrPage(string qrImg, string secret) => Shell("QR do 2FA — MtrxSys", $$"""
<h1><span class="brand">Mtrx</span>Sys</h1>
<p class="sub">Escaneie no app autenticador</p>
<img class="qr" src="{{qrImg}}" alt="QR do 2FA">
<p class="keynote">Não conseguiu escanear? Use esta chave manual:</p>
<div class="key">{{Enc(secret)}}</div>
<p class="foot"><a href="/">Voltar ao login</a></p>
""");

// ─────────────────────────── infra ───────────────────────────────────────────
sealed class SessionStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private string? _active;
    public SessionStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path));
                if (d is not null) d.TryGetValue("active", out _active);
            }
        }
        catch { /* estado inicial vazio */ }
    }
    public string? Active
    {
        get { lock (_lock) { return _active; } }
        set
        {
            lock (_lock)
            {
                _active = value;
                try
                {
                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, string?> { ["active"] = value }));
                    File.Move(tmp, _path, overwrite: true);
                }
                catch { /* best-effort */ }
            }
        }
    }
}

sealed class Throttle(int maxFails, TimeSpan window, TimeSpan lockFor)
{
    private readonly object _lock = new();
    private int _fails;
    private DateTimeOffset _first;
    private DateTimeOffset _lockedUntil;
    public bool Locked(out int secs)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _lockedUntil) { secs = (int)Math.Ceiling((_lockedUntil - now).TotalSeconds); return true; }
            secs = 0; return false;
        }
    }
    public void Fail()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _first > window) { _first = now; _fails = 0; }
            _fails++;
            if (_fails >= maxFails) { _lockedUntil = now + lockFor; _fails = 0; }
        }
    }
    public void Reset() { lock (_lock) { _fails = 0; _lockedUntil = default; } }
}
