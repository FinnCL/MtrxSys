# Aparelho físico — passo a passo

Runbook para ligar um **celular Android real** ao sistema e disparar por ele, em vez de usar o
emulador. Para o *porquê* e o desenho, ver [engine-physical.md](engine-physical.md); aqui é só o
como-fazer.

> **Estado hoje (2026-07-30):** o disparo pelo aparelho físico é por **linha de comando**: uma
> mensagem avulsa com `mtrx phone send` (seção C), ou uma lista inteira com o console interativo
> (seção D). Não há botão na tela. A variante da aba "Celular" é a Fase 4 do desenho e ainda não
> existe, e os botões Iniciar/Parar do painel comandam o dispatcher, que hoje só está ligado ao
> emulador.

---

> ⚠️ **Os blocos deste runbook são PowerShell** (`$env:`, `&`, `Invoke-WebRequest`). Colados no
> Prompt de Comando eles dão `'$env:' não é reconhecido como um comando interno`, um erro por linha.
> Confira o prompt: `PS C:\...>` é PowerShell, `C:\...>` é `cmd`. Para trocar sem abrir outra janela,
> digite `powershell` e Enter.

---

## A. No celular (uma vez por aparelho)

1. **Colocar o chip** no aparelho.
2. **Configurações → Sobre o telefone → Informações do software** → tocar **7 vezes** em
   **"Número de compilação"** (em One UI mais antigo o rótulo é "Número da versão"). Vai pedir o PIN.
   - Se estiver perdido, use a lupa das Configurações e busque `compilação`.
   - O campo certo é o único cujo valor é um código longo tipo `TP1A.220624.014.A146MUBS4CXH1`.
     **"Versão de banda base" não é.**
3. **Configurações → Opções do desenvolvedor** → ligar **Depuração USB**.
4. Na mesma tela → ligar **Permanecer ativo** (a tela não apaga enquanto carrega).
5. **Configurações → Segurança e privacidade** → desligar o **Bloqueador automático**.
6. **Play Store** → instalar o **WhatsApp** → registrar o chip.
   - Instale pela loja, não por sideload. O `build-golden-image-a.sh` registra que instalação fora da
     loja já foi suspeita de disparar o aviso "Baixe o app oficial".
   - Se oferecerem **restaurar backup do Google Drive**, **recuse**.
7. Se precisar de conta Google para a Play Store, crie uma **nova, exclusiva desse aparelho**.
   ⚠️ **Nunca reuse a conta de outro chip.** Medido em 2026-07-29: logar a conta antiga num aparelho
   recém-limpo trouxe 118 contatos de volta em 46 segundos e refez o vínculo que a limpeza desfizera.
   A conta Google segue o **chip**, não o parque de aparelhos.

## B. No computador (uma vez)

8. Ter o **adb** (`platform-tools` do Android SDK). Não tem instalador: é um zip que você extrai.
   No `cmd`:
   ```
   curl -L -o "%TEMP%\platform-tools.zip" https://dl.google.com/android/repository/platform-tools-latest-windows.zip
   if not exist "%LOCALAPPDATA%\Android\Sdk" mkdir "%LOCALAPPDATA%\Android\Sdk"
   tar -xf "%TEMP%\platform-tools.zip" -C "%LOCALAPPDATA%\Android\Sdk"
   "%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" version
   ```
   ⚠️ Ele **não entra no PATH** por padrão no Windows. Extraia nesse caminho exato:
   `C:\Users\<voce>\AppData\Local\Android\Sdk\platform-tools\adb.exe`. É um dos lugares que o
   `phone-console.ps1` procura sozinho, então ali você não precisa configurar nada. Em qualquer outro
   lugar, passe `-AdbPath` ou defina `Phone__AdbPath`.

   E o **.NET SDK 10** também é obrigatório para o console (passo 13 compila o `mtrx.exe` com ele).
   Do zero num PC limpo, incluindo Docker e virtualização:
   [pc-novo-com-aparelho-fisico.md](pc-novo-com-aparelho-fisico.md).
8b. **ADB Keyboard** — obrigatório se as mensagens tiverem **acento ou emoji**.

   Existem três canais de digitação, e o driver escolhe o melhor disponível
   (`WhatsAppUiDriver.ResolveTypingChannelAsync`):

   | Canal | Digita acento e emoji | Destinatário vê "digitando…" |
   |---|---|---|
   | **IME (ADB Keyboard)** | ✅ | ✅ |
   | `input text` (padrão do Android) | ❌ | ✅ |
   | deep link (`Phone__HumanTyping=false`) | ✅ | ❌ |

   Sem o IME você fica preso entre perder os emoji ou perder o "digitando…". Com ele, os dois
   funcionam juntos.

   **Com o celular plugado, um comando resolve** (e ele instala o `platform-tools` do passo 8 se
   ainda faltar, então serve como passo único num PC novo):

   ```
   tools\preparar-aparelho.cmd
   ```

   Com mais de um celular, escolha pelo serial: `tools\preparar-aparelho.cmd RQ8WB048RFW`.

   Na mão, se preferir:

   ```
   adb install ADBKeyboard.apk
   adb shell ime enable com.android.adbkeyboard/.AdbIME
   adb shell ime list -s
   ```

   O APK vem do projeto **ADB Keyboard** (`senzhk/ADBKeyBoard` no GitHub). O terceiro comando tem que
   listar `com.android.adbkeyboard`, que é exatamente o que o código procura.
   ⚠️ O APK legítimo tem só **17 KB** (é um IME sem recursos gráficos). Não conclua que o download
   falhou por ele ser pequeno; página de erro se reconhece pelo conteúdo, não pelo tamanho.

   ⚠️ **Habilite, mas NUNCA defina como teclado padrão.** Ele não desenha teclas, só recebe texto por
   broadcast: como padrão, o teclado **some da tela** do celular e você não consegue mais digitar nele
   à mão — e o sintoma ("o teclado sumiu") não aponta pra cá. Não é preciso: o driver seleciona o IME
   só em volta da digitação e **restaura o teclado anterior** depois (`SelectTypingImeAsync`).

   O sistema **não** instala esse APK sozinho, por decisão explícita: instalar aplicativo à revelia num
   celular de uso real é invasivo.

9. Usar **cabo de DADOS**, não de carga.
   **Teste rápido:** o Explorador de Arquivos do Windows precisa abrir o celular. Se não abrir, o
   cabo não transfere dados e o adb não tem chance.
10. Conectar e **aceitar o pop-up na tela do celular**, marcando **"Sempre permitir deste computador"**.
    - Sem a marcação, a autorização vale só até desconectar.
    - Se o pop-up não aparecer: Opções do desenvolvedor → **Revogar autorizações de depuração USB** →
      desconectar e reconectar.
11. **Descobrir o serial** — é ele que identifica o aparelho:
    ```powershell
    & "C:\...\platform-tools\adb.exe" devices
    # List of devices attached
    # RQ8WB048RFW   device        ← este é o serial
    ```
    `unauthorized` = falta aceitar o pop-up (passo 10). Nada listado = ver a seção de armadilhas.
12. **Energia** (o piloto roda por horas; PC dormindo mata o envio no meio):
    ```powershell
    powercfg /change standby-timeout-ac 0
    powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 `
             48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0
    powercfg /setactive SCHEME_CURRENT
    ```
    E na interface: **Painel de Controle → Opções de Energia → "Escolher o que a tampa faz" →
    Conectado → "Não fazer nada"**. Sem isso, fechar o notebook suspende a máquina.

    > Apagar a **tela** não é problema: o Windows continua rodando e o adb continua conectado. O que
    > mata é a **suspensão**.

## C. Enviar

13. Compilar o CLI (o executável **não vem no clone** — `bin/` é gitignored):
    ```powershell
    dotnet build MtrxSys.slnx -c Release
    # ou, para um exe portátil sem depender do .NET instalado:
    dotnet publish src/MtrxSys.Cli -c Release -r win-x64 --self-contained
    ```
14. Definir as variáveis. **O serial é o que escolhe o aparelho:**
    ```powershell
    $env:Phone__Engine    = "physical"
    $env:Phone__AdbSerial = "RQ8WB048RFW"
    $env:Phone__AdbPath   = "C:\...\platform-tools\adb.exe"
    ```
15. Enviar:
    ```powershell
    .\src\MtrxSys.Cli\bin\Release\net10.0\mtrx.exe phone send --to 5511987654321 --text "oi"
    ```
    Opções: `--dry-run` (não toca no aparelho), `--save-contact`, `--min-delay` / `--max-delay`
    (default 150–360s), e `--to` repetido para vários (teto de **10 por execução**).

    Saída esperada:
    ```
    Aparelho: running (running=True)
    enviado 5511987654321 (entrega: delivered)
    concluído sem falhas
    ```

> ⚠️ **`mtrx phone send` é BANCADA, não operação.** Ele não tem fila, teto diário, curva de
> aquecimento, opt-out, deduplicação nem auditoria — tudo isso mora no `DispatchEngine`, que hoje não
> está ligado ao aparelho físico. Use para testar mecânica, não para campanha.

## D. Console interativo (lista de contatos + variantes de texto)

O `phone send` resolve uma mensagem. Para uma **lista**, existe o console.

> **Grava sem disparar.** O **`gravar`** (ou `g`) grava a lista inteira na agenda do aparelho **sem
> enviar nada**.
> Gravar antes é melhor que deixar o `enviar` gravar: ele grava 2s antes de cada mensagem, e o contato
> ainda precisa descer pela conta Google até o WhatsApp do aparelho (é a mesma espera de 180s que o
> `DispatchEngine` faz). Grave o lote, espere alguns minutos, dispare depois.
>
> ⚠️ O console **não expande spintax**. `{a|b}` chega literal ao destinatário; só o `{nome}` é
> substituído. Variação aqui se faz com **vários templates**, e cada contato sorteia um. Colar spintax
> agora dispara um aviso na hora.



```powershell
tools\phone-console.cmd
```

O atalho acha o adb, lista os aparelhos plugados, deixa você escolher um, seta `Phone__Engine`,
`Phone__AdbSerial` e `Phone__AdbPath` sozinho e abre o `mtrx phone console` preso àquele serial.
Para pular o menu: `tools\phone-console.cmd -Serial RQ8WB048RFW`.

Ao abrir, ele **imprime sozinho o passo a passo** e depois um **menu numerado** com o valor atual de
cada ajuste e o conteúdo já gravado. Não é preciso saber nenhum comando de antemão: digita-se o
número. O menu é redesenhado depois de cada ação, então toda alteração aparece registrada na hora.

```
╭───┬───────────────────────┬─────────────────────────────────╮
│ 1 │ ritmo entre mensagens │ 300-700s                        │
│ 2 │ teto por lote         │ 7                               │
│ 3 │ gravar na agenda      │ ligado                          │
│ 4 │ contatos              │ 2 na lista                      │
│ 5 │ textos (variantes)    │ 2 variante(s)                   │
│ 6 │ ver                   │ confere o que está carregado    │
│ 7 │ previa                │ quem recebe qual texto          │
│ 8 │ enviar                │ dispara o lote (pergunta antes) │
│ 9 │ ajuda                 │ o passo a passo explicado       │
│ 0 │ sair                  │ fecha (tudo fica salvo)         │
╰───┴───────────────────────┴─────────────────────────────────╯
textos gravados:
  1 Ola {nome}, tudo bem?
  2 Oi {nome}, bom dia
```

Pelo menu, cada ajuste **pergunta o valor** em vez de exigir a sintaxe. Os comandos por extenso
continuam valendo para quem já sabe o que quer:

| comando | o que faz |
|---|---|
| `contatos` / `contatos +` | cola a lista (substitui / soma). Formato `numero` ou `numero;nome` |
| `textos` / `textos +` | cola as variantes. `{nome}` vira o nome do contato |
| `ver` | mostra lista, variantes e ajustes |
| `previa` | simula quem receberia qual variante, sem tocar no aparelho |
| `enviar` | pré-voo, plano, confirmação e disparo |
| `intervalo <min> <max>` | segundos entre um envio e o próximo (default 150 360) |
| `teto <n>` | máximo de mensagens por lote (default 30) |
| `agenda` | liga/desliga gravar o contato na agenda antes de enviar (**ligado** por padrão) |
| `ajuda` / `comandos` | o passo a passo explicado / a lista seca de comandos |
| `limpar [contatos\|textos\|tudo]`, `status`, `sair` | |

Cada contato recebe uma variante **sorteada**, não em rodízio. Rodízio distribui exato, mas cria a
regularidade (contato 1 = variante A, contato 4 = variante A…) que variar o texto tenta desfazer.

O que ele protege, e o `phone send` não:

- **valida ao colar**: número fora de 12–13 dígitos é rejeitado com o motivo, repetido é descartado
- **aponta o acento na hora**, e barra o lote inteiro no pré-voo antes da primeira mensagem
- **recusa `{nome}` com contato sem nome**, em vez de mandar "Ola , tudo bem?"
- **grava CSV linha a linha** em `%LOCALAPPDATA%\MtrxSys\phone-console\envios-<serial>.csv`, então
  fechar a janela no meio do lote não apaga quem já recebeu
- **guarda a sessão** em `<serial>.json` na mesma pasta: uma lista de 80 contatos colada à mão não se
  perde ao fechar
- **reserva o aparelho**: enquanto um console está aberto num serial, o atalho marca esse celular como
  "já aberto em outro console" e nem oferece, e um segundo console apontado na mão para o mesmo serial
  recusa a subir

> ⚠️ Continua sendo **bancada**. O console tem teto por lote, pré-voo e log, mas não tem fila, curva
> de aquecimento nem opt-out. Campanha de verdade é o `DispatchEngine`, ainda não ligado ao físico.
>
> Sobre **repetir envio**, o que existe hoje: quem recebe **sai da lista na hora** (gravado em disco a
> cada entrega, então queda de energia não devolve ninguém), e recolar a mesma lista dispara o aviso
> "já receberam mensagem deste aparelho antes", conferindo as duas formas do número. Mas o aviso
> **avisa e deixa decidir**, não remove. E ele é **por aparelho**: quem recebeu pelo celular A não
> deixa rastro no celular B. Um disparo feito aqui também **não aparece no painel**, então uma
> campanha montada depois pelo CRM incluiria essas pessoas.

---

## E. Quando o app responde "sem conta"

O erro `sem conta` significa que **o WhatsApp mostrou na tela** que aquele número não tem conta. Não é
o aparelho, não é o cabo, não é a conexão. O driver leu a tela e encontrou a frase lá.

Isso **não quer dizer que o veredito esteja certo**. Há três causas possíveis e elas pedem consertos
opostos:

1. **A lista tem número morto mesmo.** É rotina, acontece.
2. **Cache envenenado do app.** Um número gravado na agenda um dia em forma que o WhatsApp não resolve
   faz ele marcar como "sem conta" **de forma persistente**, porque a resposta fica em cache. Depois
   disso ele nega aquele número para sempre, mesmo para quem existe e está ativo. Medido em
   2026-08-05, e é por isso que o `SaveContactAsync` hoje grava em E.164 com o `+`.
3. **Falso positivo daqui.** A frase pode aparecer na tela noutro contexto.

### Como descobrir qual é

**Abra o WhatsApp no celular e procure o contato.** Se ele existe lá com conversa disponível, o
problema não é a lista. Leva 30 segundos e elimina as três dúvidas.

**Olhe a tela salva.** Todo veredito de "sem conta" grava o XML da tela em
`%LOCALAPPDATA%\MtrxSys\telas\`, e o caminho vem na própria mensagem de erro (e no CSV). É o que
permite conferir depois o que o app mostrou de verdade, sem ninguém ter estado na frente do aparelho.

- Guarda as **20 mais recentes** e apaga as antigas, então não enche disco.
- Telas idênticas viram um arquivo só. Se o diálogo mostrar o número, cada contato gera um arquivo
  diferente e o teto de 20 vale.
- Configurável em `Phone__UiDumpDir` e `Phone__UiDumpKeep`.

> 🔴 **Esses arquivos não são anônimos.** O XML é a árvore da tela: pode conter número, nome do
> contato e trechos de conversa visíveis. Olhe antes de mandar para alguém.

### Vários seguidos é outro problema

Um número negado não diz nada sobre o próximo. **Três seguidos dizem**: a chance de três acasos em
sequência é pequena perto da chance de haver causa comum. Ao cruzar esse limiar o console **avisa e
continua**, e repete o aviso a cada 10 recusas enquanto a sequência durar (um aviso só, no 3º contato,
deixaria os 84 seguintes em silêncio).

Além da lista ruim e do cache envenenado, entra aqui a causa mais séria: **a conta pode estar
restringida**. Conta restringida **deixa de resolver número**, então todo contato volta como "sem
conta", inclusive quem existe e está na agenda. O veredito do driver continua tecnicamente certo (o
app disse isso) e a causa fica invertida.

> 🔴 **A restrição aparece no WhatsApp Web e costuma NÃO aparecer no aparelho.** Confirmado em
> 2026-08-10. Abra o Web com esse chip antes de concluir qualquer coisa sobre a lista.

**Se estiver restringido, pare de disparar desse chip até normalizar.** Insistir com a conta sob
restrição é o que transforma restrição temporária em banimento. `Ctrl+C` interrompe na hora, e
`parar 5` faz o lote parar sozinho da próxima vez.

### Por que o console não decide isso sozinho

Porque **o aparelho não tem como saber**. A operação é automatizada direto no celular, sem WAHA no
caminho, e não existe sonda possível:

- A restrição quebra a resolução de **número desconhecido**. Abrir uma conversa **já existente** não
  passa por esse caminho, então responderia "está tudo bem" mesmo com a conta restrita.
- Um número nunca contatado testaria o caminho certo, mas o app **cacheia** a resposta localmente, e a
  segunda consulta viria da memória.
- Sem root não há acesso ao estado interno do app.

Ou seja, o único sinal disponível no aparelho é o sintoma ambíguo ("todo número é recusado"), e
nenhum ajuste de limiar resolve ambiguidade: só troca qual erro se comete. Quem tem a informação é
você, olhando o Web. Por isso o console informa bem e deixa a decisão com você.

Chegou a existir uma versão que **parava** o lote nesse ponto. Foi revertida: três recusas seguidas
acontecem com frequência sem restrição nenhuma, e travar interrompia o fluxo no caso comum. O ritmo já
protege o que precisava ser protegido, voltando aos 150-360s normais depois de 3 falhas, então o lote
não dispara em rajada mesmo seguindo.

---

## Vários aparelhos

O serial isola. Plugou um segundo celular? Abra `tools\phone-console.cmd` de novo: ele lista os
aparelhos, marca em cinza os que já estão abertos em outra janela e só deixa escolher um livre. Se
sobrar exatamente um livre, escolhe sozinho e avisa. Cada janela fica presa a um aparelho, com sua
própria lista e suas próprias variantes.

A reserva é o **handle** de um arquivo `<serial>.lock`, não o conteúdo dele. Isso importa porque
console morto no tranco (janela fechada no X, queda de energia) não deixa trava presa: o Windows
fecha o handle junto com o processo, e o aparelho volta a aparecer livre sozinho.

Na mão, é o mesmo princípio, um bloco de variáveis por terminal:

```powershell
# chip-a.ps1
$env:Phone__Engine    = "physical"
$env:Phone__AdbSerial = "RQ8WB048RFW"
$env:Phone__AdbPath   = "C:\...\platform-tools\adb.exe"
```

Depois `. .\chip-a.ps1` e os comandos. As variáveis são por processo, então dois aparelhos rodam em
paralelo sem se atrapalhar.

⚠️ **Não rode dois comandos contra o MESMO serial ao mesmo tempo.** O `uiautomator dump` grava num
arquivo fixo dentro do aparelho; dois processos disputando o mesmo celular leriam a tela um do outro.
Dentro de um processo há lock; entre processos, só o `phone console` reserva o aparelho. O
`phone send` **não** reserva nada, então dois `phone send` no mesmo serial ainda se atrapalham.

Serial errado **não envia pelo aparelho errado** — devolve `unavailable` e recusa. Isso é
proposital: o `DirectAdbRunner` sempre passa `-s <serial>`, mesmo com um aparelho só.

---

## 🔴 Armadilhas medidas (todas custaram tempo em 2026-07-29)

| Sintoma | Causa | Conserto |
|---|---|---|
| `adb devices` vazio, e o Windows mostra `Dispositivo USB Desconhecido (Falha na Solicitação de Descritor)` com `VID_0000&PID_0002` | **cabo só de carga** ou mau contato | trocar o cabo por um de dados |
| Aparelho aparece como `WPD` mas some a `ADB Interface` | Depuração USB desligada, **ou tela bloqueada** (Android 15 + Bloqueador automático cortam dados por USB) | ligar a depuração; desbloquear; desligar o Bloqueador |
| `unauthorized` | pop-up não aceito, ou aceito sem marcar "sempre permitir" | revogar autorizações e reconectar, marcando a caixa |
| Engine diz `unavailable` mas o cabo está bom | `adb` não resolve pelo PATH e `Phone__AdbPath` não foi setado | apontar o caminho completo do `adb.exe` |
| Todo envio falha com "digitação humana exigida mas indisponível" | texto tem **acento ou emoji** e o IME Unicode não está instalado. O `input text` do Android não digita esses caracteres | instalar o ADB Keyboard **ou** `Phone__HumanTyping=false` (aí o texto vai pronto, sem simular digitação) |
| `O CONSOLE NAO ABRIU (codigo -2147450733)` logo depois da linha `adb: ...`, com `Invalid runtimeconfig.json` acima | o `mtrx.runtimeconfig.json` está com **zero byte**. Não é o .NET da máquina: é o build que chegou pela metade, ou porque a cópia para `%LOCALAPPDATA%\MtrxSys\bin` morreu no meio, ou porque o projeto veio de outro PC **com a pasta `bin\` junto** (build não viaja) | apagar `%LOCALAPPDATA%\MtrxSys\bin` e `src\MtrxSys.Cli\bin`, e recompilar com `dotnet build MtrxSys.slnx -c Release`. Para levar o projeto a outra máquina, use o `empacotar-limpo.ps1`, que exclui `bin` e `obj` de propósito |

O último aparece no **pré-voo**, antes do lote começar, justamente para não descobrir na mensagem 1
de 200.

---

## O que NÃO dá para fazer sem root

Celular de varejo não tem `su 0`, e três capacidades do engine do emulador dependem disso:

- **veredito primário de existência** (`wa.db`) — no físico só resta o espelho da agenda, e por isso
  `IsOnWhatsAppAsync` nunca devolve `false`, só `true` ou "não sei"
- **ouvir mensagens recebidas** (`msgstore.db`) — Fase 3, ainda não implementada
- **listar grupos e participantes** — importar grupo continua pelo stack do emulador

Não rootear o aparelho: o root derruba o Play Integrity, que é justamente a vantagem principal do
celular físico sobre o emulador.

---

## Onde ler mais

- [engine-physical.md](engine-physical.md) — desenho, fases, decisões e o que foi medido
- [phone.md](phone.md) / [phone-lifecycle.md](phone-lifecycle.md) — o aparelho virtual (emulador)
- `README.md` — subir o sistema (Docker, ambientes, logins)
