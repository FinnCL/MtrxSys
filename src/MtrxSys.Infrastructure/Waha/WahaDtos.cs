namespace MtrxSys.Infrastructure.Waha;

// DTOs de desserialização da API WAHA, compartilhados pelos clientes por responsabilidade.
internal sealed record SessionDto(string? Name, string? Status, MeDto? Me, SessionConfigDto? Config);
internal sealed record MeDto(string? Id, string? PushName);
// config.proxy.server da sessão — o proxy REALMENTE aplicado (null = sem proxy). Pro indicador na aba.
internal sealed record SessionConfigDto(SessionProxyDto? Proxy);
internal sealed record SessionProxyDto(string? Server);
internal sealed record LidDto(string? Lid, string? Pn);
internal sealed record QrRawDto(string? Value);
internal sealed record PairingCodeDto(string? Code);
internal sealed record ChatLastMessageDto(string? Body, long? Timestamp);
internal sealed record ChatOverviewDto(string? Id, string? Name, ChatLastMessageDto? LastMessage);
internal sealed record MessageDto(string? Id, string? From, string? Author, bool? FromMe, string? Body, long? Timestamp);
internal sealed record GroupIdDto(string? Server, string? User, string? RawId);
internal sealed record GroupDto(GroupIdDto? Id, string? Name, List<ParticipantDto>? Participants);
internal sealed record ParticipantDto(GroupIdDto? Id, string? PushName, string? Role);
internal sealed record JoinResponseDto(string? Id, string? Name);
