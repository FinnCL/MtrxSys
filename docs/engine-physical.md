# Engine `physical` — disparo por aparelho FÍSICO via adb

Objetivo: dirigir um celular Android **real** com os mesmos comandos que hoje dirigem o emulador,
eliminando de uma vez três problemas que o emulador não resolve — atestação (Play Integrity), IP de
datacenter (proxy IPRoyal) e ausência de tctoken.

---

## Por que isto existe (medido em 2026-07-29)

No mesmo dia, o mesmo tipo de mensagem, resultados opostos:

| | Emulador (stack A) | Aparelho físico (Galaxy A14) |
|---|---|---|
| Chip | registrado 28/07 | registrado no mesmo dia, 11:02 |
| Destinatário na agenda | **sim**, `is_whatsapp_user=1` | **não** (agenda com 4 contatos) |
| Já havia conversado antes | não | não |
| Mensagem | `Ei` | `oi` |
| Resultado | **um traço cinza**, 40+ min parado | **Entregue** |

O chip do emulador tinha a defesa completa (contato salvo, sincronizado, LID resolvido pelo servidor)
e mesmo assim não entregou. O chip físico, com horas de vida e sem o contato na agenda, entregou na
primeira mensagem da vida.

⚠️ **n=1.** Não é prova, é o dado que justifica investir no caminho. O número do chip TIM pode ter
histórico anterior que não é observável daqui.

## O teste de mecânica (2026-07-29, Galaxy A14 SM-A145M, Android 15, WhatsApp 2.26.27.85, SEM root)

Toda a cadeia de envio funcionou sem uma linha de código novo:

| Etapa | Comando | Resultado |
|---|---|---|
| Abrir a conversa | `am start -a android.intent.action.VIEW -d 'whatsapp://send?phone=…'` | ✅ caiu em `com.whatsapp/.Conversation`, **mesmo nome do emulador** |
| Ler a tela | `uiautomator dump` | ✅ **não exige root** |
| Achar o campo | `com.whatsapp:id/entry` | ✅ mesmo resource-id |
| Focar e digitar | `input tap` + `input text` | ✅ |
| Botão enviar | `com.whatsapp:id/send` | ✅ surge só quando há texto, como o código já assume |
| Confirmar envio | `HasSendButton` (botão sumir) | ✅ microfone voltou no lugar |
| Estado de entrega | `content-desc` do `id/status` | ✅ leu "Entregue" |

Nenhum resource-id quebrou, apesar de a versão do app ser mais nova que a do emulador (2.26.26.70).

⚠️ Confirmado no físico o mesmo comportamento já documentado em `DockerCliPhoneOrchestrator.cs:1097`:
campo vazio **não** é `text=""`, é a DICA (`text="Mensagem"`). Quem comparar com string vazia trava
para sempre. O critério certo continua sendo o `HasSendButton`.

---

## Desenho

Separar **o que fazer no aparelho** de **como falo com o aparelho**.

```
                     ├─ DockerAdbRunner  → docker exec <container> adb shell …
IAdbRunner ──────────┤
                     └─ DirectAdbRunner  → adb -s <serial> shell …

IPhoneOrchestrator ──┬─ DockerCliPhoneOrchestrator   (ciclo de vida por Docker)
                     ├─ RedroidPhoneOrchestrator
                     └─ PhysicalPhoneOrchestrator    ← novo
```

`DockerCli.RunAsync(exe, ct, args)` já aceita qualquer executável (o engine redroid já roda `adb`
direto), então `DirectAdbRunner` é essencialmente um wrapper de `RunAsync("adb", ct, ["-s", serial, …])`.

### O contrato já suporta isto sem mudar

`IPhoneOrchestrator` tem **default em quase tudo**: apenas 8 membros são obrigatórios
(`GetStatusAsync`, `IsBootedAsync`, `ProvisionAsync`, `StartAsync`, `StopAsync`, `GetLogsAsync`,
`InstallWhatsAppAsync`, `SetProxyAsync`). Todo o resto já devolve "não suportado".

Consequência: **nenhum consumidor muda**. `DispatchEngine`, `PhoneEndpoints` e
`EmulatorInboundPollerService` recebem `IPhoneOrchestrator` por injeção e não sabem que existe engine novo.

### Mapa dos 8 obrigatórios no físico

| Membro | No físico |
|---|---|
| `GetStatusAsync` | `adb get-state`; `ViewUrl` = scrcpy em vez de noVNC |
| `IsBootedAsync` | `getprop sys.boot_completed` (idêntico) |
| `ProvisionAsync` | **no-op** — não se provisiona celular por software |
| `StartAsync` / `StopAsync` | no-op (ou `KEYCODE_WAKEUP` / `KEYCODE_SLEEP`) |
| `GetLogsAsync` | `logcat -d -t N` em vez de `docker logs` |
| `InstallWhatsAppAsync` | orienta a instalar pela Play Store; sideload é possível mas indesejado |
| `SetProxyAsync` | funciona, mas **não é usado**: o IP já é brasileiro |

---

## 🔴 O que se perde sem root, e por que importa

Celular de varejo não tem root. O `grep "su 0"` no orquestrador atual mostra que o root serve a
**uma** coisa: ler os bancos privados do WhatsApp (`DockerCliPhoneOrchestrator.cs:1391`, `:1407`, `:1438`).

| Capacidade | Hoje (root) | Substituto no físico |
|---|---|---|
| `GetWhatsAppAccountStateAsync` | dump das `shared_prefs` | `dumpsys account \| grep com.whatsapp` — distingue registrado de não registrado. **Perde o número**, então `GetWhatsAppNumberAsync` fica vazio |
| `IsOnWhatsAppAsync` | `wa.db` + agenda | só a agenda (o espelho `com.whatsapp` no contacts provider). Funciona sem root, mas perde a fonte primária |
| `ReadInboundMessagesAsync` | `msgstore.db` | `dumpsys notification --noredact`, ou `NotificationListenerService` num app próprio |
| `ListGroupsAsync` | `msgstore.db` | leitura de UI. Caro e frágil — **fica como não suportado**; importar grupo pelo stack do emulador |

### ⚠️ O ponto mais perigoso: `IsOnWhatsAppAsync`

Sem o `wa.db`, o veredito volta a depender **só do espelho da agenda** — a mesma fonte que em
2026-07-27 ficou vazia por ~19h e fez o motor descartar 10 contatos bons. E o veredito é **terminal**
(`MarkSkipped`).

No engine físico este método deve ser **conservador por construção**: devolver `null` ("não sei",
adia) em toda dúvida, e `false` apenas com afirmação positiva do aparelho. Nunca herdar a lógica de
carência do engine do emulador sem a fonte primária que a sustentava.

### Decisão consciente: `IsEgressProxyUpAsync` devolve `true`

É um portão **fail-closed**: a UI só libera registrar o chip quando ele é `true`, para o número nunca
sair pelo IP do datacenter.

No físico esse risco **não existe** — o aparelho sai pela operadora ou pelo WiFi, ambos brasileiros.
O engine devolve `true` porque a pós-condição é outra, **não** porque o teste foi afrouxado.
Não "consertar" isto achando que é bug.

---

## Fases

**Fase 1 (piloto).** `IAdbRunner`, `DirectAdbRunner`, `WhatsAppUiDriver`, `WhatsAppContactsReader`,
`PhysicalPhoneOrchestrator`, + ramo no DI e `AdbSerial` nas options.

⚠️ **A Fase 1 NÃO toca no `DockerCliPhoneOrchestrator`.** O caminho do emulador está em produção nos
10 stacks; extrair 600 linhas dele agora seria risco sem ganho. Aceita-se **duplicação temporária** da
lógica de UI. A desduplicação é passo separado, feito só depois que o caminho físico se provar — e se
não se provar, o código novo se apaga sem deixar rastro.

**Fase 2.** `IsOnWhatsAppAsync` conservador e `GetWhatsAppAccountStateAsync` por `dumpsys account`.

**Fase 3.** Inbound por `dumpsys notification`.

**Fase 4.** Variante da aba Celular (some Provisionar/Resetar/Limpar/Ligar/Desligar; tela vem do scrcpy).

## Configuração

```
Phone__Engine=physical
Phone__AdbSerial=<serial do adb devices>
Phone__AdbPath=<caminho do adb; default "adb" pelo PATH>
Phone__ViewUrl=<url do scrcpy>
```

⚠️ `AdbPath` não é conveniência. "adb está no PATH" é premissa que falha nos dois ambientes que
importam: no Windows o platform-tools do Android SDK não entra no PATH por padrão, e em container o
PATH não tem adb nenhum. Quando falha, o `Process.Start` estoura, o runner devolve -1 e o engine
reporta "unavailable" — indistinguível de cabo solto. Custou uma rodada de diagnóstico em 29/07.

`ContainerName`, `VolumeName`, `NoVncPort` e `EmulatorAdditionalArgs` deixam de ser usados.

## Onde roda

O engine mora na `MtrxSys.Infrastructure`, compartilhada pela Api e pelo Dispatcher — ou seja, roda
**onde o dispatcher rodar**. Como o `adb` precisa alcançar o celular pelo USB, no piloto isso é a
máquina local com o aparelho conectado.

Por isso o piloto usa um **runner enxuto sem banco**: lê uma lista, chama o driver, imprime o
resultado. Responde "o físico entrega?" sem subir Postgres nem mexer no deploy.

## Armadilhas operacionais medidas

1. **Cabo só de carga** derruba tudo com `VID_0000&PID_0002` / "Falha na Solicitação de Descritor".
   Custou a primeira tentativa em 29/07. Sintoma: o aparelho aparece como `WPD` mas some a `ADB Interface`.
2. **Android 15 + Bloqueador automático da Samsung** cortam dados por USB com a tela bloqueada.
   Ligar **Permanecer ativo** nas Opções do desenvolvedor e desligar o **Bloqueador automático**.
3. **Registrar um número no aparelho desregistra esse número em qualquer outro** — WhatsApp é um
   aparelho por número.
4. **Conta Google reusada refaz o vínculo** que a limpeza desfez: em 29/07, logar a conta antiga num
   aparelho recém-limpo trouxe 118 contatos de volta em 46 segundos. Conta Google segue o **chip**,
   não o parque de aparelhos.
