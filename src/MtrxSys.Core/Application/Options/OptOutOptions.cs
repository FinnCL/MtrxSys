namespace MtrxSys.Core.Application.Options;

public sealed class OptOutOptions
{
    public const string SectionName = "OptOut";

    // URL pública por onde o DESTINATÁRIO alcança a API deste ambiente (ex.: https://meu-dominio.com).
    // É usada pra montar o link de "sair" de 1 clique no rodapé da 1ª mensagem.
    // PREENCHIDO (prod) = o link é o ÚNICO caminho ANUNCIADO na mensagem: ele bate direto na nossa API
    // e funciona mesmo sem o companion WAHA. (Responder "SAIR" continua sendo DETECTADO quando há
    // inbound — só não é mais prometido, porque prometer um caminho que pode estar fora faz quem quer
    // sair denunciar.) VAZIO = link DESLIGADO (estado localhost, onde o destinatário não alcançaria a
    // API): cai no DispatchOptions.OptOutFooter. Cada stack aponta pra SUA URL (1 chip = 1 ambiente).
    public string? PublicBaseUrl { get; set; }
}
