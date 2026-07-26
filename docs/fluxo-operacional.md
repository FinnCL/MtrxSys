# Fluxo operacional: do chip vazio ao primeiro disparo

Documento vivo. Nasceu do teste de ponta a ponta de **2026-07-26** no stack A, e existe para (a) orientar o
operador e (b) servir de base para o **gating de botões por pré-condição** (a UI só oferecer o que o estado
do sistema permite).

Cada afirmação aqui foi **medida**, não deduzida. Onde não foi, está marcado como ⏳ PENDENTE.

---

## 1. O modelo mental: um contato precisa existir em TRÊS lugares

A confusão que mais custou tempo foi tratar isso como uma coisa só. São três, independentes, e **o operador
só controla o primeiro**:

| lugar | significa | quem escreve | quando |
|---|---|---|---|
| **Banco do MtrxSys** | "este contato existe e pertence ao chip X" | o operador, pela UI | ao importar/adicionar |
| **Agenda do Android** | "o aparelho tem esse número salvo" | o **dispatcher**, via adb | durante o disparo |
| **Google Contacts** | "a conta do chip conhece esse número" | o **dispatcher**, via People API | durante o disparo |

### 🔑 "Sincronizar" NÃO é um passo do operador

Não existe botão de sincronizar, e **não deve existir**. Gravar 100 contatos de uma vez na agenda é padrão de
robô; o sistema dilui essas escritas no ritmo do envio de propósito. Elas acontecem **por contato, durante o
disparo**.

Consequência para a UI: o passo a passo deve espelhar **o que o operador decide**, não o que o sistema faz.
Sincronizar não é decisão — é consequência. Listá-lo como passo cria expectativa de um botão que não deve
existir. (Esta pergunta foi feita 3× por um operador experiente antes de a distinção ficar clara.)

---

## 2. Importar × Migrar — a hierarquia importa mais que a ordem

| | **Importar de grupos** | **Migrar para este chip** |
|---|---|---|
| O que faz | **descobre** um vínculo que já existe | **declara** um vínculo por decreto |
| Evidência | o chip ESTÁ no grupo — o WhatsApp sabe | só o nosso banco sabe |
| Risco 463 | baixo | **é exatamente o cenário de 463** |
| Papel na UI | caminho padrão, visível | saída de exceção, com o risco escrito |

**Migrar não faz parte de um fluxo saudável.** É ação corretiva para quando re-importar é impossível — contato
adicionado à mão nunca veio de grupo nenhum, e a re-importação não o alcança.

> A pergunta que decide: **o WhatsApp tem como saber que esse número me conhece?** Se a resposta vier do nosso
> banco em vez de um grupo, uma conversa ou um contato salvo e sincronizado, a defesa que foi desligada era a
> que estava certa.

---

## 3. Fluxo para APARELHO NOVO (o caminho saudável)

```
1. Registrar o chip no emulador
2. Conceder READ_CONTACTS + WRITE_CONTACTS   ← sem isso: espelho vazio = 0 envios
3. Entrar em grupos COM este chip
4. Importar dos grupos (aba Grupos)          ← aqui nasce o vínculo REAL
5. Validar números (aba Contatos, passo 2)   ← descarta inexistentes. PRECISA de WAHA WORKING
6. Preparar disparo, escolhendo a lista
7. Iniciar
   └─ por contato e automático:
      salva no Google → grava na agenda → espera o espelho → envia
```

**Migrar não aparece neste fluxo.**

### Fluxo CORRETIVO (sem grupos para importar)

Quando o aparelho tem 0 grupos e os contatos vieram de outro chip:

```
1. Limpar fila (aba Disparo)          ← torna o passo 3 inofensivo
2. Adicionar números → lista própria
3. Migrar para este chip
4. Preparar disparo ESCOLHENDO a lista ← o escopo é o que protege
5. Retomar a fila
```

⚠️ **A ordem não é cosmética.** Migrar re-etiqueta TODOS os contatos. Se houver jobs `Pending` dos contatos
antigos, retomar a fila os enviaria. Limpar a fila primeiro é o que impede isso.

---

## 4. O que acontece por dentro depois do "Iniciar" (tempos MEDIDOS)

```
Job entra no motor
  ↓ salva no Google Contacts → não sincronizou ainda → ADIA 180s
  ↓ [3 min] grava na agenda do Android (adb)
  ↓ confere o espelho com.whatsapp → não apareceu → ADIA 480s
  ↓ [8 min] espelho populou (2,5–7 min típico) → confirma que tem WhatsApp
  ↓ envia pela UI do emulador → lê o status na tela
```

**A primeira mensagem em aparelho de agenda vazia leva ~11 minutos.** Os dois adiamentos **parecem falha no
log e são sucesso**. A tolerância antes de desistir é 20 min (`MirrorSyncGrace`), propositalmente muito maior
que os 8 min da re-pergunta (`EmulatorSyncGraceSeconds`) — as duas já foram iguais e um sync lento virava
descarte definitivo do contato.

Fontes: `AddressBookSync__GraceSeconds=180`, `DispatchEngine.EmulatorSyncGraceSeconds=480`,
`DockerCliPhoneOrchestrator.MirrorSyncGrace=20min`.

---

## 5. Matriz de pré-condições (base para o gating de botões)

| ação | pré-condição real | onde ler | se faltar |
|---|---|---|---|
| Enviar pelo **Chat** | sessão WAHA `WORKING` | `waha.GetSessionStatusAsync` | **409** — o endpoint recusa |
| **Validar números** | sessão WAHA `WORKING` | idem (usa `CheckNumberExistsAsync`) | aborta após 5 indeterminadas |
| **Importar de grupos** | aparelho tem grupos | banco do aparelho (`jid.server='g.us'`) | nada para importar |
| **Disparar** (qualquer) | chip registrado | `registration_jid` nos shared_prefs | sem remetente |
| **Disparar** para alguém | `ImportedByPhone == chip` | banco | pulado (`OtherChip`) |
| Espelho popular | READ/WRITE_CONTACTS | `dumpsys package com.whatsapp` | **0 envios**, todo número "não existe" |
| Defesa Google | `Provider=Google` + `RefreshToken` | env do **dispatcher** | job segue sem a defesa |

### 3 regras para implementar o gating (aprendidas doendo)

1. **Desabilitar sem dizer por quê cria um beco novo.** Botão cinza sem explicação é tão ruim quanto botão que
   falha. O padrão é *"desabilitado **porque** X — faça Y primeiro"*, visível sem hover.
2. **O sensor tem que ler a fonte certa.** O selo do Google (`/api/phone/gsync-status`) lê a config da **api**,
   que não recebe `AddressBookSync__*` — só o dispatcher recebe. Ele reporta "desligado" com a defesa ligada.
   Gating sobre um sensor assim trancaria botões por motivo falso. **Corrigir antes de gatilhar qualquer coisa.**
3. **Leitura que falhou não pode desabilitar.** adb mudo = estado *desconhecido*, não *ausente*. O código já
   segue isso (`connectedPhone null → não bloqueia`). Desabilitar no desconhecido transforma blip de infra em
   sistema travado.

**Ordem sugerida:** primeiro **mostrar** o estado (painel de pré-condições), depois **gatilhar**. Mostrar é
reversível; gatilhar mal trava. O problema hoje é invisibilidade, não excesso de cliques.

---

## 6. Armadilhas de reversibilidade (medidas)

| ação | reversível? | como |
|---|---|---|
| **Limpar fila** | ✅ sim | apaga só `Pending`/`Retrying`; "Preparar disparo" reconstrói. Não toca contatos nem histórico |
| **Renovar** | ⚠️ pesado | apaga TODOS os jobs **e** zera `LastSentAt` — quem recebeu volta a "Novo" |
| **Descartar contatos da lista** | 🔴 **só por re-importação** | o modal diz "é reversível", mas o único caminho que desfaz `deleted_at` é `ReimportInto` — **não há endpoint nem botão de restaurar**. Com 0 grupos, não tem volta pela UI |

⚠️ O texto do modal de descarte **promete mais do que a UI entrega**. Ou se implementa "Restaurar", ou o texto
precisa dizer que a volta é por re-importação do grupo.

### Interações de sequência (ações seguras isoladamente, perigosas juntas)

- **Migrar DEVOLVE à fila** os jobs pulados por `OtherChip` (`RequeueSkippedByChipGateAsync`). Limpar a fila e
  migrar em seguida pode se anular. Em 26/07 os 9 pulados eram todos "número não existe no WhatsApp" → nenhum
  ressuscitou, mas isso foi **medido**, não deduzido. **Conferir o motivo dos pulados antes de migrar.**

---

## 7. Estado do stack A em 2026-07-26 (referência do teste)

| item | valor |
|---|---|
| Chip | `557193919318`, registrado e saudável |
| Modo | `Emulator` |
| READ/WRITE_CONTACTS | concedidas |
| Grupos no aparelho | **0** |
| Agenda / espelho | **vazios** (device limpo pela imagem-ouro) |
| Conta Google **no aparelho** | não existe (não é necessária — a defesa é server-side) |
| Sessão WAHA | `FAILED` |
| Teto do dia | 3–5 (curva `[3,5,8,…,200]`, chip do dia 25/07) |
| Fase Humana / "só respondeu" / guard de entrega | **as três desligadas** |

### O que já foi PROVADO neste aparelho (tudo com o WAHA `FAILED`)

- 🟢 **Ouvir sem WAHA** — mensagem recebida ingerida pelo poller; marco 48→49; `@lid` resolvido para o
  telefone real; timestamp correto; contato criado e vinculado.
- 🟢 **Enviar sem WAHA** — 1 mensagem pelo emulador: `{"ok":true,"delivery":"delivered"}`.
- 🟢 **Opt-out ponta a ponta** — link `/s/` no fio (200, sem passar pelo portão), POST `/sair/confirm` aceito,
  chaves JWT idênticas entre api e dispatcher, rota legada viva.
- 🟢 **MOTOR COMPLETO DE DISPARO** — o ciclo inteiro, observado no log:
  ```
  [16:16:02] Sessão WAHA Failed: sigo enviando pelo EMULADOR
  [16:16:12] check-exists WAHA → 422 (10s de timeout — corrigido depois em c7e520e)
  [16:16:15] Checagem pelo APARELHO: +557182368724 tem WhatsApp    ← espelho respondeu
  [16:16:24] Emulador: enviado para +557182368724
  ```
  Confirmado no banco: job `Retrying` → `Sent`, `send_audit_log` +1. O adiamento de 8 min do ciclo
  anterior **não era falha** — era o motor esperando o espelho popular, e valeu.

### 🔑 O caminho que ninguém tinha previsto: o Google DEVOLVE contatos

Ao logar a conta Google no emulador (`type=com.google`, verificado em `dumpsys account` **e** no
`contacts/settings`), **117 contatos desceram da nuvem para a agenda em 3,3s** — a agenda estava vazia.
Conferidos por amostragem contra o banco: são os da lista `BÔNUS BBS-ESPORTES JOÃO CARLOS`.

Eles estavam lá porque o `EnsureSavedAsync` do servidor os empurrou em disparos anteriores, **pelo chip
antigo**. A conta Google é o repositório compartilhado entre chips. **Sincronização é bidirecional** — o
sistema vinha alimentando aquele repositório havia semanas sem que ninguém percebesse que ele era também
uma FONTE.

Efeito prático: em aparelho novo, logar a conta importa a base inteira de uma vez e **elimina os
adiamentos** de quem já está lá. Depois disso, reiniciar o WhatsApp faz o espelho popular (medido: 112 de
117 em ~3 min; os 5 restantes não têm WhatsApp).

⚠️ O contato que o **dispatcher** grava (via adb) cai com `account_type = NULL`, e não em `com.google`
como o código espera. Ou seja: ele **não sobe** para o Google pelo aparelho e some num `pm clear`. A
defesa via servidor (People API) segue funcionando — é caminho separado. Não investigado.

### ⏳ PENDENTE

- **Testes de integração** não rodaram na sessão (Docker local desligado) — são os que cobririam o poller
  ponta a ponta.
- **Reiniciar o WhatsApp não tem botão** — só por adb. O usuário final não consegue fazer o espelho
  reler a agenda, e o botão vizinho ("Trocar chip") **destrói a conta**. Ver a matriz da seção 5.
- **Sem contadores de espelho na tela** — `117 na agenda / 113 reconhecidos` só existe via SSH.
