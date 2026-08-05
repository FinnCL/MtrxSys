# Migrar o ambiente para outro PC

Passo a passo para sair do zero, num computador novo, e chegar no mesmo estado funcional.

---

## Caminho rápido: `instalar.cmd`

O `instalar.cmd` na raiz faz os passos 2 a 5 sozinho. Ele confere o git, clona se precisar, confere
o Docker (e **abre o Docker Desktop** se estiver fechado, esperando até 3 minutos), cria o `.env` com
uma chave JWT aleatória de 64 caracteres, avisa se alguma porta está ocupada e chama o `start.cmd`.

Se você **já clonou**, é só dar duplo clique nele. Se **ainda não clonou**, baixe só esse arquivo
numa pasta vazia e rode; ele clona o projeto do lado e continua:

```powershell
irm https://raw.githubusercontent.com/FinnCL/MtrxSys/main/instalar.cmd -OutFile instalar.cmd
.\instalar.cmd
```

Pré-requisitos que ele **não** instala por você: **Docker Desktop** (obrigatório) e **Git** (só no
caso de ainda não ter clonado). Ele avisa com o link se faltar algum.

> ⚠️ Rode num PC que **ainda não tem** o MtrxSys. O nome do projeto do Compose vem do nome da pasta,
> então clonar um segundo `MtrxSys` na mesma máquina faz os dois brigarem pelos mesmos containers, e
> quem subir por último recria os do outro com o próprio `.env`. Medido, não suposto.

O resto deste documento é o mesmo caminho na mão, e continua valendo para entender o que o script faz
e para consertar quando algo sair do previsto.

---

## A ideia que faz o resto ficar óbvio

**O código viaja. O estado, não.**

O `git clone` traz tudo que é *receita*: código, scripts, configuração de exemplo, documentação. Ele
não traz nada que é *resultado*: senhas, banco de dados, sessões, binários compilados, autorizações.

Guarde essa separação e cada passo abaixo vira consequência dela.

| Vem no clone ✅ | NÃO vem ❌ | Por quê |
|---|---|---|
| Código-fonte | `bin/`, `obj/` (executáveis) | são gerados, ficam no `.gitignore` |
| `.env.example` | `.env` (com as senhas de verdade) | segredo em repositório é falha de segurança |
| Scripts `.cmd` | Volumes do Docker (banco, contatos, histórico) | são dados, vivem fora do repositório |
| Docs | Sessão do WhatsApp / WAHA | é vínculo com o aparelho, não é arquivo |
| | Autorização do adb | é uma chave por PC, aprovada no celular |

---

## Passo 1 — Instalar os pré-requisitos

| O quê | Para quê | Como conferir |
|---|---|---|
| **Docker Desktop** (compose v2+) | roda banco, API e painel | `docker version` responde |
| **.NET SDK 10** | compila o backend e o CLI | `dotnet --version` mostra 10.x |
| **Git** | clonar | `git --version` |
| **platform-tools** (adb) | *só se for usar celular físico* | ver o doc do aparelho físico |

> **Ligue o Docker Desktop antes de seguir.** Ele não sobe sozinho, e sem ele nada funciona.

## Passo 2 — Clonar

```powershell
git clone https://github.com/FinnCL/MtrxSys.git
cd MtrxSys
```

## Passo 3 — Criar o `.env` (é aqui que quase todo mundo trava)

O repositório traz um `.env.example` com **38 variáveis**. Copie e preencha:

```powershell
Copy-Item .env.example .env
notepad .env
```

O que é obrigatório para simplesmente **subir e ver funcionando**:

| Variável | O que colocar |
|---|---|
| `PG_PASS` | qualquer senha; é o banco local. Ex.: `mtrx` |
| `JWT_SIGNING_KEY` | uma string longa e aleatória (≥ 32 caracteres) |
| `WAHA_API_KEY` | qualquer valor; ex.: `mtrxsys-dev-key` |
| `WAHA_DASH_USER` / `WAHA_DASH_PASS` | qualquer login; é o painel do WAHA |

O que pode ficar **vazio** por enquanto:

- `WAHA_PROXY_1` até `WAHA_PROXY_10` (e usuário/senha de cada) — só importam ao conectar chip de verdade
- Variáveis de `ADDRBOOK_` / Google — só para o sync de agenda

> ⚠️ **O `JWT_SIGNING_KEY` precisa ser o MESMO na api e no dispatcher.** Como os dois leem do mesmo
> `.env`, isso sai de graça. Só não invente de ter dois arquivos.

## Passo 4 — Subir

```powershell
start.cmd
```

Primeira vez leva **2 a 5 minutos** (está compilando as imagens). Depois, ~10 segundos.

Para os 10 ambientes de uma vez:

```powershell
up-all.cmd     # ~61 containers e vários GB de RAM — suba só se precisar
```

## Passo 5 — Abrir e conferir

| Ambiente | Painel | Login |
|---|---|---|
| A | http://localhost:5173 | `admin@local` / `admin123!` |
| B | http://localhost:5174 | `admin-b@local` / `chipB123!` |
| … até J | 5183 | `admin-<letra>@local` / `chip<Letra>123!` |
| Landing (com `up-all`) | http://localhost:5175 | campos já preenchidos |

Se o painel abriu e você conseguiu logar, **o ambiente está de pé**.

## Passo 6 — Compilar o CLI (se for usar linha de comando)

O executável não vem no clone:

```powershell
dotnet build MtrxSys.slnx -c Release
```

Gera `src\MtrxSys.Cli\bin\Release\net10.0\mtrx.exe`.

---

## E os dados? (contatos, templates, histórico)

**Não vão junto.** Vivem em volumes do Docker, que são do computador, não do repositório.

Você tem três opções, da mais simples à mais trabalhosa:

### Opção 1 — Começar limpo (recomendado)
Não faça nada. O banco nasce vazio, as migrações aplicam sozinhas na primeira subida, e você importa
os contatos de novo pela aba Grupos.

É a opção certa na maioria dos casos: base de contato velha costuma ser justamente o que você não
quer levar para um chip novo.

### Opção 2 — Levar o banco
No PC antigo:
```powershell
docker exec -e PGPASSWORD=<senha> mtrx-postgres pg_dump -U mtrx -d mtrx > backup.sql
```
No PC novo, depois do `start.cmd`:
```powershell
Get-Content backup.sql | docker exec -i -e PGPASSWORD=<senha> mtrx-postgres psql -U mtrx -d mtrx
```

### Opção 3 — Levar tudo, inclusive o emulador
Copiar os volumes do Docker (`pgdata`, `waha-sessions`, `android-data`). É trabalhoso e frágil.
⚠️ E levar o `android-data` **carrega junto a identidade do aparelho virtual**, que é exatamente o
que se quer quebrar quando um chip é restringido. Pense duas vezes.

> **Armadilha real:** a sessão do WhatsApp **não se copia**. Ela é um vínculo entre o número e um
> aparelho, mantido no servidor da Meta. Migrando de PC, o chip precisa ser pareado de novo.

---

## Se for usar celular físico

O ambiente acima não cobre isso. Siga o
[aparelho-fisico-passo-a-passo.md](aparelho-fisico-passo-a-passo.md), que trata de:

- preparar o celular (depuração USB, permanecer ativo, Bloqueador automático)
- preparar o PC (adb, cabo de dados, autorização, energia)
- descobrir o serial e enviar

⚠️ A **autorização do adb é por computador**. No PC novo, o celular vai pedir de novo o
"Permitir depuração USB?". Marque **"Sempre permitir deste computador"**.

---

## Ajustes da máquina (só se for operar de verdade)

Se o PC vai ficar rodando disparo por horas:

```powershell
powercfg /change standby-timeout-ac 0
powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 `
         48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0
powercfg /setactive SCHEME_CURRENT
```

E na interface: **Painel de Controle → Opções de Energia → "Escolher o que a tampa faz" →
Conectado → "Não fazer nada"**.

> Apagar a **tela** é inofensivo; o que mata o envio no meio é a **suspensão** da máquina.

---

## Checklist final

- [ ] Docker Desktop **ligado**
- [ ] `.NET SDK 10` instalado
- [ ] Repositório clonado
- [ ] `.env` criado a partir do `.env.example`, com `PG_PASS`, `JWT_SIGNING_KEY` e `WAHA_API_KEY`
- [ ] `start.cmd` rodou sem erro
- [ ] Painel abre em http://localhost:5173 e o login funciona
- [ ] *(opcional)* `dotnet build MtrxSys.slnx -c Release` para ter o `mtrx.exe`
- [ ] *(opcional)* celular físico configurado pelo doc próprio
- [ ] *(opcional)* energia ajustada, se o PC for ficar operando

---

## Problemas comuns

| Sintoma | Causa provável | Conserto |
|---|---|---|
| `docker compose` falha logo de cara | Docker Desktop não está ligado | abrir e esperar ficar verde |
| Painel abre mas o login não passa | `JWT_SIGNING_KEY` vazio ou diferente entre serviços | preencher no `.env` e `down-all.cmd` + `start.cmd` |
| Erro de coluna inexistente no dispatcher | imagem antiga: a api acha que o banco está em dia | `docker compose build api` e subir de novo |
| Build do .NET falha | SDK errado | `dotnet --version` precisa mostrar 10.x |
| Portas ocupadas (5173, 5080, 5432) | outro processo usando | fechar, ou ajustar as portas no compose |

> A terceira linha da tabela é medida: em 2026-07-29 o ambiente local estava **6 migrações atrás**
> porque a imagem da api era de 11 dias antes, e ela logava "Banco já está na última migration"
> achando que estava em dia. Imagem velha mente com convicção.
