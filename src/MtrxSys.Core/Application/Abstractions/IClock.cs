namespace MtrxSys.Core.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly TodayUtc => DateOnly.FromDateTime(UtcNow.UtcDateTime);

    // "Dia operacional" do anti-ban de disparo: o dia-calendário de Brasília (UTC-3 fixo — o
    // Brasil não tem horário de verão desde 2019). Sem isto o contador diário zeraria à meia-noite
    // UTC = 21h de Brasília, no meio do horário de pico, deixando estourar até 2x o teto numa mesma
    // noite. Mesmo critério que o JoinThrottle já aplica à entrada em grupo.
    // Método ESTÁTICO de propósito: recebe o instante (sempre o UtcNow mockável), então o cálculo
    // roda de verdade nos testes — um default interface method seria substituído por valor padrão.
    static DateOnly ToBrasiliaDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(-3)).DateTime);
}
