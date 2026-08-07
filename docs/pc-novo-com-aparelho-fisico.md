# PC novo, do zero até disparar pelo aparelho físico

Runbook do caminho completo, na ordem em que ele realmente acontece: máquina limpa, sem nada
instalado, até o console do celular aberto.

Os outros dois documentos cobrem metades disto e continuam valendo:
[migrar-para-outro-pc.md](migrar-para-outro-pc.md) para o painel, e
[aparelho-fisico-passo-a-passo.md](aparelho-fisico-passo-a-passo.md) para o celular e o console.
Este aqui é a **costura entre os dois**, escrito depois de percorrer o caminho num PC de verdade em
2026-08-05. Todos os erros listados abaixo aconteceram; nenhum é hipotético.

---

## O que precisa estar instalado (e o que não precisa)

| | Precisa? | Para quê |
|---|---|---|
| **Docker Desktop** | sim | o sistema inteiro roda em container |
| **Git** | sim | clonar |
| **.NET SDK 10** | **sim, se for usar o aparelho físico** | compila o `mtrx.exe`, que o console executa |
| **adb** (platform-tools) | sim, idem | é quem fala com o celular |
| Node, Postgres, .NET Runtime | **não** | tudo isso vive dentro das imagens |

> ⚠️ O `migrar-para-outro-pc.md` chama o .NET SDK de *opcional*, e está certo **para o painel**. Para
> o console do aparelho ele é obrigatório: `tools/phone-console.ps1` aborta sem o `mtrx.exe`, e o
> `mtrx.exe` não vem no clone porque `bin/` é gitignored.

---

## ⚠️ Antes de tudo: `cmd` ou PowerShell?

Metade dos erros deste runbook são o mesmo erro: comando de um shell colado no outro.

Olhe o começo da linha antes de colar qualquer coisa:

| Prompt | Shell | Sintaxe |
|---|---|---|
| `C:\...>` | `cmd.exe` | `%VAR%`, sem `$` |
| `PS C:\...>` | PowerShell | `$var`, `Invoke-WebRequest`, `&` |

Colar PowerShell no `cmd` produz `'$zip' não é reconhecido como um comando interno`, uma linha por
comando, o que parece cinco problemas e é um só. Para trocar de shell sem abrir outra janela, digite
`powershell` e Enter. Os blocos abaixo estão marcados com o shell de cada um.

---

## Etapa 1 — Docker

1. Baixe em https://www.docker.com/products/docker-desktop/ e instale. Ele pede para reiniciar o
   Windows; reinicie de verdade.
2. Abra o Docker Desktop. Na tela de login, **pode pular** ("Continue without signing in"). Conta não
   é obrigatória.
3. Espere o ícone da baleia, na bandeja do sistema ao lado do relógio, ficar estável. O rodapé do
   Docker Desktop precisa dizer **"Engine running"**.

### Se aparecer "Virtualization support not detected"

O Docker no Windows roda os containers dentro de uma VM Linux, então ele depende de virtualização
por hardware. O botão "Sign in" que a própria tela oferece **não resolve isso**; é propaganda mal
colocada.

Descubra em qual caso você está: Gerenciador de Tarefas (Ctrl+Shift+Esc) → Desempenho → CPU → linha
**Virtualização**.

- **"Desabilitado"** → está desligado na BIOS. Configurações → Sistema → Recuperação → "Inicialização
  avançada" → Reiniciar agora → Solução de Problemas → Opções Avançadas → **Configurações de Firmware
  UEFI**. Lá dentro, habilite `Intel Virtualization Technology` / `Intel VT-x` (Intel) ou `SVM Mode` /
  `AMD-V` (AMD). Salve com F10.
- **"Habilitado"** → falta o Windows. PowerShell **como administrador**: `wsl --install`, depois
  reinicie.

Muito PC de fabricante vem com a virtualização desligada de fábrica. Não é limitação do hardware, é
uma chave que ninguém virou.

---

## Etapa 2 — Clonar e subir o sistema

```powershell
git clone https://github.com/FinnCL/MtrxSys.git
cd MtrxSys
instalar.cmd
```

Se ainda não clonou nada, baixe só o `instalar.cmd` numa pasta vazia e rode: ele clona e continua
sozinho. Ver [migrar-para-outro-pc.md](migrar-para-outro-pc.md#caminho-rápido-instalarcmd).

Ao terminar, o painel abre em http://localhost:5173 (`admin@local` / `admin123!`) com o banco vazio.

Confirme o estado real, que é o que importa, e não a ausência de mensagens vermelhas:

```powershell
docker ps -a --filter "name=mtrx-" --format "{{.Names}} {{.Status}}"
```

### Se aparecer `pull access denied for mtrxsys-waha-emulator`

A imagem do emulador é **construída localmente** e não existe em registro nenhum. O serviço `waha` a
referencia por nome, sem seção `build`, então o Compose tenta baixá-la do Docker Hub antes de o
serviço `waha-emulator-build` tê-la criado.

O `instalar.cmd` passou a construir essa imagem antes de subir, então o erro não deve mais aparecer.
Se aparecer em outro fluxo:

```powershell
docker compose build waha-emulator-build
docker compose up -d
```

---

## Etapa 3 — .NET SDK 10 e o `mtrx.exe`

1. Baixe em https://dotnet.microsoft.com/download/dotnet/10.0. Coluna **SDK**, linha **Windows x64**.
   O "Runtime" não compila nada.
2. **Feche o prompt e abra outro.** O PATH é lido quando o terminal nasce; janela aberta antes da
   instalação não enxerga o `dotnet`.
3. Confirme: `dotnet --version` precisa mostrar `10.x`.
4. Compile:

```powershell
cd C:\caminho\para\MtrxSys
dotnet build MtrxSys.slnx -c Release
```

O sintoma de pular esta etapa é o `mtrx.cmd` responder `No .NET SDKs were found`, e o
`phone-console.cmd` parar em `mtrx.exe nao encontrado`.

> ⚠️ **Não teste com `mtrx --version`.** O CLI tem um comando padrão, e qualquer argumento que ele não
> reconheça, `--version` inclusive, cai nesse padrão, que exige o WAHA no ar e responde *"conexão
> recusada (localhost:3000)"*. Parece instalação quebrada e não é. Teste com `mtrx phone --help`.

### Se o projeto veio de outro PC com a pasta `bin\` junto

Compile de novo **nesta** máquina, e apague antes o que veio pronto:

```powershell
Remove-Item "$env:LOCALAPPDATA\MtrxSys\bin" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item ".\src\MtrxSys.Cli\bin" -Recurse -Force -ErrorAction SilentlyContinue
dotnet build MtrxSys.slnx -c Release
```

`bin\` é produto de build, não é fonte: ele não viaja bem, e uma cópia de rede interrompida deixa
arquivo com zero byte do lado de cá. O `mtrx.runtimeconfig.json` vazio é o caso clássico, e derruba o
programa antes da primeira linha rodar. Para levar o projeto adiante, use o `empacotar-limpo.ps1`, que
exclui `bin` e `obj` exatamente por isso.

---

## Etapa 4 — Instalar o adb

Não tem instalador: é um zip que você extrai no lugar certo. E o lugar certo importa, porque
`phone-console.ps1` procura nele antes de desistir.

**Destino:** `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`

### No `cmd`

```
curl -L -o "%TEMP%\platform-tools.zip" https://dl.google.com/android/repository/platform-tools-latest-windows.zip
if not exist "%LOCALAPPDATA%\Android\Sdk" mkdir "%LOCALAPPDATA%\Android\Sdk"
tar -xf "%TEMP%\platform-tools.zip" -C "%LOCALAPPDATA%\Android\Sdk"
"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" version
```

### No PowerShell

```powershell
$zip = "$env:TEMP\platform-tools.zip"
$destino = "$env:LOCALAPPDATA\Android\Sdk"
Invoke-WebRequest "https://dl.google.com/android/repository/platform-tools-latest-windows.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $destino -Force
& "$destino\platform-tools\adb.exe" version
```

Deu certo quando a última linha responde `Android Debug Bridge version 1.0.41` (ou mais nova).

⚠️ Extraindo na mão, arraste a **pasta** `platform-tools` para dentro de `Sdk`, não o conteúdo dela.
Terminar com `...\Sdk\adb.exe` ou `...\Sdk\platform-tools\platform-tools\adb.exe` faz o script não
achar.

---

## Etapa 5 — O celular

O preparo do aparelho está em [aparelho-fisico-passo-a-passo.md](aparelho-fisico-passo-a-passo.md),
seção A, e não se repete aqui. O que este runbook acrescenta é o diagnóstico de quando o PC **não
identifica** o celular.

Teste:

```
"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" devices
```

Você quer o serial seguido de `device`. Se não vier, abra o Gerenciador de Dispositivos
(`devmgmt.msc`) com o celular plugado e leia a assinatura:

| No Gerenciador de Dispositivos | Causa | Correção |
|---|---|---|
| `Dispositivo USB Desconhecido (Falha na Solicitação de Descritor)` | cabo só de carga, ou mau contato | trocar por cabo de dados |
| aparece como dispositivo portátil, mas sem `ADB Interface` | Depuração USB desligada, tela bloqueada, ou Bloqueador automático | ligar a depuração, desbloquear a tela, desligar o Bloqueador automático |
| nada aparece, e o celular nem avisa que carrega | cabo ou porta USB morta | trocar os dois |
| lista o serial com `unauthorized` | pop-up não aceito no celular | aceitar marcando **"Sempre permitir deste computador"** |

Depois de mexer em qualquer coisa, derrube o cache do adb antes de testar de novo:

```
"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" kill-server
"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" devices
```

⚠️ A autorização do adb é **por computador**. Um celular já autorizado em outra máquina vai perguntar
de novo aqui.

---

## Etapa 6 — Abrir o console

```
cd C:\caminho\para\MtrxSys
tools\phone-console.cmd
```

Ou por duplo clique, em `tools\phone-console.cmd` pelo Explorador. O `.cmd` existe exatamente para
isso: `.ps1` não abre por duplo clique no Windows.

O que ele faz e o menu que apresenta estão documentados em
[aparelho-fisico-passo-a-passo.md](aparelho-fisico-passo-a-passo.md#d-console-interativo-lista-de-contatos--variantes-de-texto).

### Um atalho por aparelho

Uma janela opera um aparelho. Para não digitar o serial toda vez:

1. Botão direito em `tools\phone-console.cmd` → Enviar para → **Área de trabalho (criar atalho)**
2. Propriedades do atalho → campo **Destino** → acrescente o serial no fim:
   `C:\...\MtrxSys\tools\phone-console.cmd -Serial RQ8WB048RFW`
3. Renomeie para o nome do chip

Duplo clique passa a abrir direto naquele celular, sem menu. Como o `.cmd` resolve a própria
localização, o atalho funciona de qualquer pasta.

⚠️ **Nunca duas janelas no mesmo serial.** O `uiautomator dump` grava num arquivo fixo dentro do
aparelho, e os dois processos leriam a tela um do outro. Seriais **diferentes** em paralelo, sim: as
variáveis de ambiente são por processo.

---

## Checklist

- [ ] Docker Desktop com "Engine running" (virtualização habilitada na BIOS, se preciso)
- [ ] `instalar.cmd` rodou e o painel abre em http://localhost:5173
- [ ] `docker ps` mostra `mtrx-waha` com `Up`
- [ ] `dotnet --version` mostra `10.x`
- [ ] `dotnet build MtrxSys.slnx -c Release` terminou com `Build succeeded`
- [ ] `adb devices` lista o serial com `device` (não `unauthorized`)
- [ ] `tools\phone-console.cmd` abre e mostra o menu
- [ ] *(opcional)* atalho por aparelho criado
- [ ] *(opcional)* energia ajustada, se o PC for operar por horas (ver o runbook do aparelho)

---

## Erros reais deste caminho, em ordem de aparecimento

| Erro | Etapa | Significa |
|---|---|---|
| `PAROU AQUI: Docker Desktop nao esta instalado` | 1 | não instalado, **ou** instalado com o prompt já aberto (feche e abra outro) |
| `Virtualization support not detected` | 1 | BIOS ou componentes do Windows. O "Sign in" não resolve |
| `pull access denied for mtrxsys-waha-emulator` | 2 | imagem local ainda não construída. Cosmético se o `mtrx-waha` subir |
| `No .NET SDKs were found` | 3 | falta o SDK 10 |
| `mtrx.exe nao encontrado` | 3 | SDK instalado, mas faltou o `dotnet build` |
| `O CONSOLE NAO ABRIU (codigo -2147450733)`, com `Invalid runtimeconfig.json` acima | 3 | build pela metade, quase sempre `bin\` trazido de outro PC. Ver a etapa 3 |
| `conexão recusada (localhost:3000)` ao rodar `mtrx --version` | 3 | falso alarme: `--version` cai no comando padrão, que fala com o WAHA. Use `mtrx phone --help` |
| `'$zip' não é reconhecido...` | 4 | PowerShell colado no `cmd` |
| `adb devices` vazio | 5 | quase sempre cabo de carga. Ver a tabela da etapa 5 |
| `unauthorized` | 5 | pop-up esperando na tela do celular |
