namespace MtrxSys.Core.Application.Options;

public sealed class OptOutOptions
{
    public const string SectionName = "OptOut";

    // URL pública por onde o DESTINATÁRIO alcança a API deste ambiente (ex.: https://meu-dominio.com).
    // É usada pra montar o link de "sair" de 1 clique no rodapé da 1ª mensagem.
    // VAZIO = link de opt-out DESLIGADO (estado localhost, onde o destinatário não alcançaria a API):
    // só o "responda SAIR" continua valendo. Cada stack aponta pra SUA URL pública (1 chip = 1 ambiente).
    public string? PublicBaseUrl { get; set; }
}
