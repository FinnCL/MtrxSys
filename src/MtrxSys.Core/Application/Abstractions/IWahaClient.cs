namespace MtrxSys.Core.Application.Abstractions;

public interface IWahaClient
{
    Task<WahaSessionStatus> GetSessionStatusAsync(string sessionId, CancellationToken ct);
    /// <summary>Telefone E.164 do número conectado na sessão ("me"), ou null se indisponível.</summary>
    Task<string?> GetOwnPhoneE164Async(string sessionId, CancellationToken ct);
    /// <summary>Status + identidade (número/nome) da sessão numa ÚNICA leitura (evita 2 GETs).</summary>
    Task<WahaSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken ct);
    /// <summary>True se a sessão já está com o proxy DESEJADO aplicado — ou se não há proxy configurado
    /// (nada a esperar). TRAVA do QR: não servir QR de sessão sem proxy, senão o chip conecta (WORKING)
    /// sem proxy e sai pelo IP do servidor — e aplicar depois, em conta viva, deslogaria.</summary>
    Task<bool> IsProxyReadyAsync(string sessionId, CancellationToken ct);
    /// <summary>Resolve um LID (@lid, número oculto) para o telefone E.164 real, ou null se não der.</summary>
    Task<string?> ResolveLidToPhoneE164Async(string sessionId, string lid, CancellationToken ct);
    Task EnsureSessionStartedAsync(string sessionId, CancellationToken ct);
    /// <summary>Reinicia a sessão (stop+start). Necessário pra recuperar do estado FAILED, onde um simples start é rejeitado.</summary>
    Task RestartSessionAsync(string sessionId, CancellationToken ct);
    /// <summary>Desconecta o número da sessão (logout no WhatsApp). Depois é preciso parear de novo via QR.</summary>
    Task LogoutSessionAsync(string sessionId, CancellationToken ct);
    /// <summary>Apaga a sessão por completo (config + credenciais em disco). Diferente do logout, não
    /// deixa resíduo pro WAHA restaurar — garante QR novo no próximo start. Idempotente (404 = ok).</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct);
    Task<byte[]> GetQrPngAsync(string sessionId, CancellationToken ct);
    Task<string> GetQrRawAsync(string sessionId, CancellationToken ct);
    /// <summary>Pede um código de pareamento por número (NOWEB) — alternativa ao QR, imune ao timing
    /// de rotação do QR. O usuário digita o código no WhatsApp em "Conectar com número de telefone".
    /// phoneNumber deve ser só dígitos com DDI (ex.: 5571999998888). Retorna o código (ex.: "ABCD-EFGH").</summary>
    Task<string> RequestPairingCodeAsync(string sessionId, string phoneNumber, CancellationToken ct);

    Task<IReadOnlyList<WahaChat>> ListChatsOverviewAsync(string sessionId, int limit, CancellationToken ct);
    Task<IReadOnlyList<WahaMessage>> GetChatMessagesAsync(string sessionId, string chatId, int limit, CancellationToken ct);

    /// <summary>Agenda do aparelho. Devolve TODOS os contatos com a marca IsMyContact (salvo na
    /// agenda) — quem FILTRA é o chamador, de propósito: se o engine não preencher a marca, um
    /// filtro aqui viraria lista vazia inexplicável, enquanto assim o dado bruto continua visível.
    /// Exclui grupos, @lid e o próprio número. Exige o store do NOWEB, que WahaHttp.NowebConfig()
    /// já liga sempre. Melhor-esforço: sessão fora do ar devolve lista vazia, igual ao
    /// ListGroupsAsync.</summary>
    Task<IReadOnlyList<WahaContact>> ListContactsAsync(string sessionId, CancellationToken ct);

    /// <summary>Cria um grupo com os participantes indicados (E.164) e devolve o grupo criado.
    ///
    /// Existe pra o sistema SABER que o grupo é do operador: o WAHA não expõe "quem criou" (não há
    /// campo owner; só o `role` do participante, que nem o NOWEB garante). Quem cria, sabe — então
    /// criar por aqui é o que permite separar/destacar o grupo depois, sem heurística.</summary>
    Task<WahaGroup> CreateGroupAsync(
        string sessionId, string name, IReadOnlyCollection<string> participantsE164, CancellationToken ct);

    Task<IReadOnlyList<WahaGroup>> ListGroupsAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<WahaParticipant>> ListGroupParticipantsAsync(string sessionId, string groupId, CancellationToken ct);
    /// <summary>Faz o número conectado SAIR do grupo. Só 404 (já não é membro ou grupo inexistente)
    /// conta como sucesso; 422/409/5xx sobem como erro, pra não dar falso "saiu" deixando o usuário
    /// ainda no grupo.</summary>
    Task LeaveGroupAsync(string sessionId, string groupId, CancellationToken ct);
    Task<WahaGroup> JoinGroupByInviteAsync(string sessionId, string inviteCodeOrUrl, CancellationToken ct);

    Task<string> SendTextAsync(string sessionId, string phoneOrChatId, string text, CancellationToken ct);
    /// <summary>Confere se o número existe no WhatsApp e devolve o chatId CANÔNICO (o WhatsApp resolve o
    /// 9º dígito BR pra a forma real). Evita disparar pra número inexistente — que falha com erro 463 E é
    /// gatilho de ban (foi o que restringiu a conta no teste). Retorna null se a checagem não pôde rodar
    /// (erro/sessão fora): aí o disparo segue, pra não descartar um contato por hiccup transitório.</summary>
    Task<WahaNumberCheck?> CheckNumberExistsAsync(string sessionId, string phoneE164, CancellationToken ct);
    /// <summary>Envia uma imagem (bytes + mimetype) com legenda opcional. Retorna o id da mensagem no WhatsApp.</summary>
    Task<string> SendImageAsync(string sessionId, string phoneOrChatId, byte[] imageData, string mimeType, string caption, CancellationToken ct);
    Task StartTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct);
    Task StopTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct);

    // webhookToken (opcional): quando presente, é gravado como customHeader X-Webhook-Token na
    // sessão, pra o WAHA enviá-lo em cada callback e passar na validação do endpoint.
    Task<bool> EnsureWebhookConfiguredAsync(
        string sessionId, string webhookUrl, IReadOnlyList<string> events, string? webhookToken, CancellationToken ct);
}

public enum WahaSessionStatus
{
    Unknown = 0,
    Stopped,
    Starting,
    ScanQrCode,
    Working,
    Failed,
}

public sealed record WahaIdentity(string PhoneE164, string? Name);

/// <summary>Resultado do check-exists do WAHA: se o número existe no WhatsApp + o chatId canônico (a
/// forma que o WhatsApp usa de fato — resolve o 9º dígito BR). ChatId é null quando não existe.</summary>
public sealed record WahaNumberCheck(bool Exists, string? ChatId);

// AppliedProxyServer = o config.proxy.server da sessão (o proxy que o chip REALMENTE usa; null = sem
// proxy). Opcional pra não quebrar os construtores existentes. Ver indicador de proxy na aba Celular.
public sealed record WahaSessionSnapshot(
    WahaSessionStatus Status, WahaIdentity? Identity, string? AppliedProxyServer = null);

/// <summary>Contato da agenda do aparelho. IsMyContact = está salvo na agenda (o WAHA também
/// devolve quem só apareceu num chat, e essa gente não interessa à Fase Humana).</summary>
public sealed record WahaContact(string PhoneE164, string? Name, bool IsMyContact);

public sealed record WahaGroup(string Id, string Name, int? ParticipantsCount);

public sealed record WahaParticipant(string Id, string PhoneE164, string? Name, bool IsAdmin);

public sealed record WahaChat(string Id, string Name, string? LastMessagePreview, DateTimeOffset? LastMessageAt, bool IsGroup);

public sealed record WahaMessage(string Id, string ChatId, bool FromMe, string Author, string Body, DateTimeOffset Timestamp);
