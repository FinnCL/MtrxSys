namespace MtrxSys.Core.Application.Abstractions;

/// <summary>Saúde do EGRESSO do emulador — o proxy residencial que impede o WhatsApp do emulador de
/// sair pelo IP do datacenter (gatilho de ban). O watchdog do host escreve um flag ("ok"/"leak") a
/// cada ciclo; o disparo lê pra decidir se pode enviar.</summary>
public enum EmulatorEgressStatus
{
    /// <summary>Gate DESLIGADO (sem caminho configurado) — o disparo não é bloqueado por isto.</summary>
    Disabled,

    /// <summary>Proxy de pé: o egresso sai pelo residencial. Pode enviar.</summary>
    Healthy,

    /// <summary>NÃO confirmado: o flag diz "leak", OU sumiu/ilegível. Fail-closed — NÃO enviar, porque
    /// a mensagem sairia pelo IP do datacenter.</summary>
    Unhealthy,
}

/// <summary>Diz se o egresso do emulador está protegido, pro disparo NÃO enviar quando o proxy
/// residencial não está de pé. FAIL-CLOSED: na dúvida (flag ausente/ilegível), devolve Unhealthy —
/// parar o disparo é sempre melhor que arriscar vazar o IP do datacenter e queimar o chip.</summary>
public interface IEmulatorEgressHealthCheck
{
    EmulatorEgressStatus Check();
}
