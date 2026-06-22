using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace MtrxSys.WahaEmulator;

/// <summary>Estado em memória do "WhatsApp emulado": sessões, grupos, contatos e mensagens.
/// Único por processo (1 container por stack), protegido por um lock simples — uso é localhost
/// de teste. Some no restart do container; é aceitável nesta fase.</summary>
public sealed class EmulatorStore(
    IOptions<EmulatorOptions> options,
    IHttpClientFactory httpFactory,
    ILogger<EmulatorStore> log)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private EmulatorOptions Opts => options.Value;

    public Session? Find(string id)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out var s) ? s : null;
        }
    }

    private Session GetOrCreate(string id)
    {
        if (!_sessions.TryGetValue(id, out var s))
        {
            s = new Session { Id = id };
            _sessions[id] = s;
        }
        return s;
    }

    /// <summary>Cria/inicia a sessão. WORKING se já autenticada ou AutoConnect; senão SCAN_QR_CODE.
    /// Pós-reset (ForceScan) sempre cai no QR. Reproduz a semântica do /start do WAHA pro app.</summary>
    public Session StartOrCreate(string id, WebhookCfg[]? webhooks = null, string? proxy = null)
    {
        lock (_gate)
        {
            var s = GetOrCreate(id);
            ApplyConfig(s, webhooks, proxy);

            if (s.Status == "WORKING")
            {
                return s;
            }
            if (s.ForceScan)
            {
                s.ForceScan = false;
                ToScan(s);
                return s;
            }
            if (s.Authenticated || Opts.AutoConnect)
            {
                Connect(s);
            }
            else
            {
                ToScan(s);
            }
            return s;
        }
    }

    public Session Connect(string id)
    {
        lock (_gate)
        {
            var s = GetOrCreate(id);
            Connect(s);
            return s;
        }
    }

    private void Connect(Session s)
    {
        s.Authenticated = true;
        s.ForceScan = false;
        s.Status = "WORKING";
        s.MePhone ??= Opts.MePhone;
        s.MePushName ??= Opts.MePushName;
        SeedIfEmpty(s);
    }

    public Session ShowQr(string id)
    {
        lock (_gate)
        {
            var s = GetOrCreate(id);
            ToScan(s);
            return s;
        }
    }

    private static void ToScan(Session s)
    {
        s.Status = "SCAN_QR_CODE";
        if (string.IsNullOrEmpty(s.QrValue))
        {
            s.QrValue = "2@" + Guid.NewGuid().ToString("N");
        }
    }

    public Session? Scan(string id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var s))
            {
                return null;
            }
            Connect(s);
            return s;
        }
    }

    public void Logout(string id)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var s))
            {
                s.Authenticated = false;
                s.Status = "STOPPED";
                s.MePhone = null;
                s.MePushName = null;
                s.QrValue = "";
            }
        }
    }

    /// <summary>Apaga as credenciais: mantém grupos/mensagens, mas força QR novo no próximo start
    /// (é o que o /reset do app espera pra mostrar um QR limpo).</summary>
    public void Delete(string id)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var s))
            {
                s.Authenticated = false;
                s.ForceScan = true;
                s.Status = "STOPPED";
                s.MePhone = null;
                s.MePushName = null;
                s.QrValue = "";
            }
        }
    }

    public void SetConfig(string id, WebhookCfg[]? webhooks, string? proxy)
    {
        lock (_gate)
        {
            ApplyConfig(GetOrCreate(id), webhooks, proxy);
        }
    }

    private static void ApplyConfig(Session s, WebhookCfg[]? webhooks, string? proxy)
    {
        if (webhooks is not null)
        {
            s.Webhooks.Clear();
            s.Webhooks.AddRange(webhooks);
        }
        if (proxy is not null)
        {
            s.ProxyServer = string.IsNullOrWhiteSpace(proxy) ? null : proxy;
        }
    }

    public Session Reseed(string id)
    {
        lock (_gate)
        {
            var s = GetOrCreate(id);
            s.Groups.Clear();
            SeedGroups(s);
            return s;
        }
    }

    private void SeedIfEmpty(Session s)
    {
        if (s.Groups.Count == 0)
        {
            SeedGroups(s);
        }
    }

    // 2 grupos com participantes BR válidos (DDI 55 + DDD 11 + 9 + 8 dígitos), pra o import
    // criar contatos reais e o disparo/opt-out funcionarem ponta-a-ponta.
    private static void SeedGroups(Session s)
    {
        s.Groups.Add(MakeGroup("120363000000000001@g.us", "Grupo Teste A", 1, 5));
        s.Groups.Add(MakeGroup("120363000000000002@g.us", "Grupo Teste B", 6, 4));
    }

    private static Group MakeGroup(string jid, string subject, int startSeq, int count)
    {
        var g = new Group { Jid = jid, Subject = subject };
        for (var i = 0; i < count; i++)
        {
            var seq = startSeq + i;
            g.Participants.Add(new Participant
            {
                PhoneDigits = $"55119876{seq:D5}",   // ex.: 5511987600001
                PushName = $"Contato {seq:D2}",
                IsAdmin = i == 0,
            });
        }
        return g;
    }

    public void LeaveGroup(string sessionId, string groupId)
    {
        var num = groupId.Split('@')[0];
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var s))
            {
                s.Groups.RemoveAll(g => g.Number == num);
            }
        }
    }

    public Group JoinGroup(string sessionId, string code)
    {
        lock (_gate)
        {
            var s = GetOrCreate(sessionId);
            var jid = $"1203630000000{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}@g.us";
            var g = MakeGroup(jid, $"Grupo Convite {code}", 80, 4);
            s.Groups.Add(g);
            return g;
        }
    }

    // ---- Leituras (sempre sob lock, copiando, pra não dar corrida com o disparo) ----

    public IReadOnlyDictionary<string, object> GroupsMap(string sessionId)
    {
        lock (_gate)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            if (_sessions.TryGetValue(sessionId, out var s))
            {
                foreach (var g in s.Groups)
                {
                    map[g.Jid] = new
                    {
                        id = g.Jid,
                        subject = g.Subject,
                        participants = ParticipantsJson(g),
                    };
                }
            }
            return map;
        }
    }

    public object[] Participants(string sessionId, string groupId)
    {
        var num = groupId.Split('@')[0];
        lock (_gate)
        {
            var g = _sessions.TryGetValue(sessionId, out var s)
                ? s.Groups.FirstOrDefault(x => x.Number == num)
                : null;
            return g is null ? [] : ParticipantsJson(g);
        }
    }

    private static object[] ParticipantsJson(Group g) =>
        [.. g.Participants.Select(p => new
        {
            id = p.ChatId,
            phoneNumber = p.ChatId,
            pushName = p.PushName,
            admin = p.IsAdmin ? "admin" : null,
        })];

    public object[] ChatsOverview(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
            {
                return [];
            }
            return [.. s.Messages
                .GroupBy(m => m.ChatId)
                .Select(grp =>
                {
                    var last = grp.MaxBy(m => m.Timestamp)!;
                    return (object)new
                    {
                        id = grp.Key,
                        name = grp.Key,
                        lastMessage = new { body = last.Body, timestamp = last.Timestamp },
                    };
                })];
        }
    }

    public object[] ChatMessages(string sessionId, string chatId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
            {
                return [];
            }
            return [.. s.Messages
                .Where(m => m.ChatId == chatId)
                .OrderBy(m => m.Timestamp)
                .Select(m => (object)new
                {
                    id = m.Id,
                    from = m.ChatId,
                    author = m.Author,
                    fromMe = m.FromMe,
                    body = m.Body,
                    timestamp = m.Timestamp,
                })];
        }
    }

    /// <summary>Estado consolidado pro painel "celular fake".</summary>
    public object Snapshot(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
            {
                return new
                {
                    session = sessionId,
                    status = "STOPPED",
                    me = (object?)null,
                    autoConnect = Opts.AutoConnect,
                    webhooks = Array.Empty<string>(),
                    groups = Array.Empty<object>(),
                    messages = Array.Empty<object>(),
                };
            }
            return new
            {
                session = s.Id,
                status = s.Status,
                me = s.MePhone is null ? null : new { phone = s.MePhone, name = s.MePushName },
                autoConnect = Opts.AutoConnect,
                webhooks = s.Webhooks.Select(w => w.Url).ToArray(),
                groups = s.Groups.Select(g => new
                {
                    jid = g.Jid,
                    number = g.Number,
                    subject = g.Subject,
                    participants = g.Participants.Select(p => new
                    {
                        phone = p.PhoneDigits,
                        name = p.PushName,
                        admin = p.IsAdmin,
                    }).ToArray(),
                }).ToArray(),
                messages = s.Messages
                    .OrderByDescending(m => m.Timestamp)
                    .Take(200)
                    .Select(m => new
                    {
                        chatId = m.ChatId,
                        fromMe = m.FromMe,
                        author = m.Author,
                        body = m.Body,
                        timestamp = m.Timestamp,
                    }).ToArray(),
            };
        }
    }

    /// <summary>Registra uma mensagem ENVIADA pelo app (disparo, confirmação de opt-out, etc.).</summary>
    public string AddOutbound(string sessionId, string chatId, string body)
    {
        var core = NewCore();
        lock (_gate)
        {
            var s = GetOrCreate(sessionId);
            s.Messages.Add(new Msg
            {
                Id = core,
                ChatId = chatId,
                FromMe = true,
                Author = s.MePushName ?? "eu",
                Body = body,
                Timestamp = Now(),
            });
        }
        return core;
    }

    /// <summary>Registra uma resposta do CONTATO e dispara o(s) webhook(s) pro app (fora do lock).
    /// É o caminho do "SAIR"/opt-out e do chat recebido.</summary>
    public async Task AddInboundAsync(string sessionId, string fromDigits, string body, string? name, CancellationToken ct)
    {
        var chatId = fromDigits + "@c.us";
        var core = NewCore();
        var ts = Now();
        List<(string Url, List<string> Events)> targets;
        string? mePhone;

        lock (_gate)
        {
            var s = GetOrCreate(sessionId);
            s.Messages.Add(new Msg
            {
                Id = core,
                ChatId = chatId,
                FromMe = false,
                Author = name ?? chatId,
                Body = body,
                Timestamp = ts,
            });
            mePhone = s.MePhone;
            targets = s.Webhooks.Count > 0
                ? [.. s.Webhooks.Select(w => (w.Url, w.Events))]
                : Opts.DefaultHookUrl is { Length: > 0 } u ? [(u, new List<string> { "message" })] : [];
        }

        foreach (var t in targets)
        {
            await FireWebhookAsync(t.Url, sessionId, chatId, mePhone, core, body, name, ts, ct);
        }
    }

    private async Task FireWebhookAsync(
        string url, string sessionId, string chatId, string? mePhone,
        string core, string body, string? name, long ts, CancellationToken ct)
    {
        var payload = new
        {
            @event = "message",
            session = sessionId,
            payload = new
            {
                id = $"false_{chatId}_{core}",
                timestamp = ts,
                from = chatId,
                to = mePhone is null ? null : mePhone + "@c.us",
                fromMe = false,
                body,
                hasMedia = false,
                notifyName = name,
            },
        };

        try
        {
            var http = httpFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(Opts.WebhookToken))
            {
                req.Headers.Add("X-Webhook-Token", Opts.WebhookToken);
            }
            using var resp = await http.SendAsync(req, ct);
            log.LogInformation("Webhook -> {Url} ({Status}) from={From}", url, (int)resp.StatusCode, chatId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Falha ao enviar webhook para {Url}", url);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string NewCore() => Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
}
