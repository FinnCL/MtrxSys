using System.Text.Json;
using MtrxSys.WahaEmulator;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<EmulatorStore>();
builder.Services.Configure<EmulatorOptions>(o =>
{
    builder.Configuration.GetSection("Emulator").Bind(o);
    // Espelha as env vars que o compose do WAHA real já passa, pra ligar o webhook/token sem
    // config extra: Webhooks__WahaToken e WHATSAPP_HOOK_URL.
    if (string.IsNullOrWhiteSpace(o.WebhookToken))
    {
        o.WebhookToken = builder.Configuration["Webhooks:WahaToken"];
    }
    if (string.IsNullOrWhiteSpace(o.DefaultHookUrl))
    {
        o.DefaultHookUrl = builder.Configuration["WHATSAPP_HOOK_URL"];
    }
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ===================== Superfície HTTP do WAHA (engine NOWEB) =====================

app.MapGet("/api/sessions/{id}", (string id, EmulatorStore store) =>
{
    var s = store.Find(id);
    return s is null ? Results.NotFound() : Results.Ok(SessionJson(s));
});

app.MapPost("/api/sessions", (JsonElement body, EmulatorStore store) =>
{
    var name = Str(body, "name") ?? "default";
    var start = body.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.True;
    var (hooks, proxy) = ParseConfig(body);
    if (start)
    {
        return Results.Ok(SessionJson(store.StartOrCreate(name, hooks, proxy)));
    }
    store.SetConfig(name, hooks, proxy);
    return Results.Ok(SessionJson(store.Find(name)!));
});

app.MapPost("/api/sessions/{id}/start", (string id, EmulatorStore store) =>
    Results.Ok(SessionJson(store.StartOrCreate(id))));

app.MapPost("/api/sessions/{id}/restart", (string id, EmulatorStore store) =>
    Results.Ok(SessionJson(store.StartOrCreate(id))));

app.MapPost("/api/sessions/{id}/logout", (string id, EmulatorStore store) =>
{
    store.Logout(id);
    var s = store.Find(id);
    return Results.Ok(s is null ? new { status = "STOPPED" } : (object)SessionJson(s));
});

app.MapDelete("/api/sessions/{id}", (string id, EmulatorStore store) =>
{
    store.Delete(id);
    return Results.Ok();
});

app.MapPut("/api/sessions/{id}", (string id, JsonElement body, EmulatorStore store) =>
{
    var (hooks, proxy) = ParseConfig(body);
    store.SetConfig(id, hooks, proxy);
    return Results.Ok(SessionJson(store.Find(id)!));
});

// ---- Auth / onboarding ----

app.MapGet("/api/{session}/auth/qr", (string session, string? format, EmulatorStore store) =>
{
    var s = store.Find(session);
    var value = s?.QrValue;
    if (string.IsNullOrEmpty(value))
    {
        value = store.ShowQr(session).QrValue; // garante um QR mesmo se chamado fora de hora
    }
    return string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase)
        ? Results.Ok(new { value })
        : Results.File(FakeQr.Png(value!), "image/png");
});

app.MapPost("/api/{session}/auth/request-code", (string session, JsonElement body) =>
    Results.Ok(new { code = "EMUL-CODE" }));

// Não usamos @lid no emulador: 404 = "não resolve" (o app trata como null, sem quebrar).
app.MapGet("/api/{session}/lids/{lid}", () => Results.NotFound());
// phone→LID: 404 no emulador → o cliente cai no @c.us (dev não tem LID real).
app.MapGet("/api/{session}/lids/pn/{phone}", () => Results.NotFound());

// ---- Chats ----

app.MapGet("/api/{session}/chats/overview", (string session, EmulatorStore store) =>
    Results.Ok(store.ChatsOverview(session)));

app.MapGet("/api/{session}/chats/{chatId}/messages", (string session, string chatId, EmulatorStore store) =>
    Results.Ok(store.ChatMessages(session, chatId)));

// ---- Grupos (forma NOWEB: /groups = objeto indexado por JID) ----

app.MapGet("/api/{session}/groups", (string session, EmulatorStore store) =>
    Results.Ok(store.GroupsMap(session)));

app.MapGet("/api/{session}/groups/{groupId}/participants", (string session, string groupId, EmulatorStore store) =>
    Results.Ok(store.Participants(session, groupId)));

app.MapPost("/api/{session}/groups/{groupId}/leave", (string session, string groupId, EmulatorStore store) =>
{
    store.LeaveGroup(session, groupId);
    return Results.Ok();
});

app.MapPost("/api/{session}/groups/join", (string session, JsonElement body, EmulatorStore store) =>
{
    var g = store.JoinGroup(session, Str(body, "code") ?? "");
    return Results.Ok(new { id = g.Jid, name = g.Subject });
});

// ---- Envio / presença ----

app.MapPost("/api/sendText", (JsonElement body, EmulatorStore store) =>
{
    var session = Str(body, "session") ?? "default";
    var chatId = Str(body, "chatId") ?? "";
    var id = store.AddOutbound(session, chatId, Str(body, "text") ?? "");
    return Results.Ok(new { id });
});

app.MapPost("/api/sendImage", (JsonElement body, EmulatorStore store) =>
{
    var session = Str(body, "session") ?? "default";
    var chatId = Str(body, "chatId") ?? "";
    var caption = Str(body, "caption") ?? "";
    var id = store.AddOutbound(session, chatId, string.IsNullOrEmpty(caption) ? "[imagem]" : caption + " [imagem]");
    return Results.Ok(new { id });
});

app.MapPost("/api/startTyping", () => Results.Ok());
app.MapPost("/api/stopTyping", () => Results.Ok());

// ===================== Painel "celular fake" (/_emulator/*) =====================

app.MapGet("/_emulator/state", (string? session, EmulatorStore store) =>
    Results.Ok(store.Snapshot(session ?? "default")));

app.MapPost("/_emulator/sessions/{id}/connect", (string id, EmulatorStore store) =>
    Results.Ok(store.Snapshot(store.Connect(id).Id)));

app.MapPost("/_emulator/sessions/{id}/qr", (string id, EmulatorStore store) =>
    Results.Ok(store.Snapshot(store.ShowQr(id).Id)));

app.MapPost("/_emulator/sessions/{id}/scan", (string id, EmulatorStore store) =>
{
    var s = store.Scan(id);
    return s is null ? Results.NotFound() : Results.Ok(store.Snapshot(s.Id));
});

app.MapPost("/_emulator/sessions/{id}/logout", (string id, EmulatorStore store) =>
{
    store.Logout(id);
    return Results.Ok(store.Snapshot(id));
});

app.MapPost("/_emulator/sessions/{id}/seed", (string id, EmulatorStore store) =>
    Results.Ok(store.Snapshot(store.Reseed(id).Id)));

app.MapPost("/_emulator/sessions/{id}/incoming", async (string id, JsonElement body, EmulatorStore store, CancellationToken ct) =>
{
    var from = Digits(Str(body, "from") ?? "");
    var text = Str(body, "body") ?? "";
    var name = Str(body, "name");
    if (from.Length == 0 || text.Length == 0)
    {
        return Results.BadRequest(new { error = "from e body são obrigatórios" });
    }
    await store.AddInboundAsync(id, from, text, name, ct);
    return Results.Ok(store.Snapshot(id));
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// ===================== Helpers =====================

static object SessionJson(Session s) => new
{
    name = s.Id,
    status = s.Status,
    me = s.Status == "WORKING" && s.MePhone is not null
        ? new { id = s.MePhone + "@c.us", pushName = s.MePushName }
        : null,
    config = new
    {
        webhooks = s.Webhooks.Select(w => new { url = w.Url, events = w.Events }).ToArray(),
        proxy = s.ProxyServer is null ? null : new { server = s.ProxyServer },
    },
};

static (WebhookCfg[]? Webhooks, string? Proxy) ParseConfig(JsonElement root)
{
    if (!root.TryGetProperty("config", out var cfg) || cfg.ValueKind != JsonValueKind.Object)
    {
        return (null, null);
    }

    WebhookCfg[]? hooks = null;
    if (cfg.TryGetProperty("webhooks", out var hw) && hw.ValueKind == JsonValueKind.Array)
    {
        hooks = [.. hw.EnumerateArray()
            .Select(e =>
            {
                var wc = new WebhookCfg { Url = e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "" };
                if (e.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in ev.EnumerateArray())
                    {
                        if (x.ValueKind == JsonValueKind.String)
                        {
                            wc.Events.Add(x.GetString()!);
                        }
                    }
                }
                return wc;
            })
            .Where(w => w.Url.Length > 0)];
    }

    string? proxy = null;
    if (cfg.TryGetProperty("proxy", out var px) && px.ValueKind == JsonValueKind.Object
        && px.TryGetProperty("server", out var sv) && sv.ValueKind == JsonValueKind.String)
    {
        proxy = sv.GetString();
    }
    return (hooks, proxy);
}

static string? Str(JsonElement e, string name) =>
    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString()
        : null;

static string Digits(string s) => new([.. s.Where(char.IsDigit)]);
