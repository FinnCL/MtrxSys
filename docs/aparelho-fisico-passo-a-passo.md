# Aparelho físico — passo a passo

Runbook para ligar um **celular Android real** ao sistema e disparar por ele, em vez de usar o
emulador. Para o *porquê* e o desenho, ver [engine-physical.md](engine-physical.md); aqui é só o
como-fazer.

> **Estado hoje (2026-07-29):** o disparo pelo aparelho físico é por **linha de comando**. Não há
> botão na tela — a variante da aba "Celular" é a Fase 4 do desenho e ainda não existe. Os botões
> Iniciar/Parar do painel comandam o dispatcher, que hoje só está ligado ao emulador.

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

8. Ter o **adb** (`platform-tools` do Android SDK).
   ⚠️ Ele **não entra no PATH** por padrão no Windows. Anote o caminho completo, ex.:
   `C:\Users\<voce>\AppData\Local\Android\Sdk\platform-tools\adb.exe`
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

---

## Vários aparelhos

O serial isola. Cada terminal (ou script) aponta para um aparelho:

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
Dentro de um processo há lock; entre processos, não.

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
