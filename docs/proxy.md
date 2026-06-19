# Proxy por chip (anti-correlação de IP)

## Resumo

> Implementamos suporte a proxy: cada chip passa a ter seu próprio IP fixo, em vez de os 10
> saírem pelo mesmo IP da máquina, pra o WhatsApp não ligar os números entre si.

**Estado atual: pronto, mas DESLIGADO por padrão.** Ativa quando você contratar os proxies e
preencher os IPs num `.env`. Enquanto vazio, o comportamento é exatamente o de hoje (sem proxy).

## O que é (em linguagem simples)

Rodamos 10 chips de WhatsApp numa **única máquina**. Quando todos disparam, eles saem pela
**mesma "porta de saída" da internet** (o IP da máquina). Pro WhatsApp, é como 10 pessoas mandando
muita mensagem com o **mesmo endereço de remetente** — fica evidente que estão juntas.

O proxy faz cada chip sair por uma **porta diferente** — um **IP próprio por chip** —, então os 10
parecem 10 usuários em lugares diferentes, sem ligação entre si.

## O conceito de IP (importante)

O WhatsApp **NÃO bane IP. Ele bane o CHIP (o número).** O IP é só o "fio" que liga um chip ao outro.

| O que é | Tem IP? | Importa pro disparo/ban? |
|---|---|---|
| **Máquina/computador** (roda o WAHA) | Sim — o IP da sua internet | ✅ É este que o WhatsApp vê no disparo |
| **Aparelho/celular** (a conta) | Sim, mas só usado pra escanear o QR | ❌ Irrelevante pro disparo |
| **Chip/número** | ❌ Não tem IP — é um número | É o que é **banido** (a conta), não é um IP |

- "IP do chip" não existe — chip = número.
- "IP do aparelho" não importa — o celular só pareia; depois pode ficar desligado.
- Se 10 chips saem do mesmo IP e um leva ban, o WhatsApp desconfia dos "vizinhos" e pode
  **derrubar os outros junto** (efeito-dominó). O proxy isola os IPs e quebra esse dominó.

## Sobre o IP do proxy

- **É real e válido, não fictício** — pertence a uma casa/rede móvel real, em algum lugar real.
- **Você ALUGA** de uma empresa de proxy (serviço pago). Usa "emprestado" enquanto paga.
- **Diferente por chip**, mas **FIXO** (sticky) — não fica mudando sozinho. IP que troca no meio
  do uso o WhatsApp acha suspeito.

### Que tipo contratar
- **Residencial ou móvel (4G/5G)** — NÃO datacenter (datacenter o WhatsApp identifica e piora).
- **IP fixo (sticky)**.
- **Do Brasil** (mesmo país dos números).
- **1 IP/porta por chip** (precisa de ~10).
- ⚠️ Proxy **grátis é pior que nenhum** (datacenter/queimado/instável).

## Como ativar

1. Crie um arquivo **`.env`** na raiz do projeto (já está no `.gitignore` — não é commitado):

   ```env
   WAHA_PROXY_1=ip-do-proxy:porta
   WAHA_PROXY_1_USER=usuario
   WAHA_PROXY_1_PASS=senha

   WAHA_PROXY_2=outro-ip:porta
   WAHA_PROXY_2_USER=usuario
   WAHA_PROXY_2_PASS=senha
   # ... até WAHA_PROXY_10
   ```

2. Recrie a **`api`** do ambiente correspondente (é ela que aplica o proxy na sessão):

   ```powershell
   docker compose -f docker-compose.yml   up -d --force-recreate api   # ambiente 1
   docker compose -f docker-compose-2.yml up -d --force-recreate api   # ambiente 2
   # ...
   ```

3. No startup, a `api` injeta o proxy no **config da sessão** do WAHA e **religa a sessão** (reusa
   a auth salva — SEM QR), pra o chip reconectar pelo IP do proxy. Variável vazia = sem proxy.

### Como funciona por baixo
> ⚠️ **NÃO use a env var `WHATSAPP_PROXY_SERVER` do WAHA.** Comprovado empiricamente: o WAHA
> 2026.x (tier **CORE**, engine **NOWEB**) a **IGNORA silenciosamente** — a sessão conecta direto
> pelo IP da máquina, sem erro e sem aviso. O proxy SÓ pega via **config da sessão** (API).

As mesmas vars `WAHA_PROXY_N(_USER/_PASS)` do `.env` agora alimentam o serviço **`api`**:

```yaml
# serviço api de cada docker-compose-N.yml
Waha__ProxyServer:   ${WAHA_PROXY_N:-}
Waha__ProxyUsername: ${WAHA_PROXY_N_USER:-}
Waha__ProxyPassword: ${WAHA_PROXY_N_PASS:-}
```

O `WahaClient` (na `api`) injeta isso no `config.proxy` da sessão — tanto ao **criar** a sessão
quanto no **ensurer de startup** (`WahaWebhookEnsurer`), que grava `config.proxy` + webhook juntos
e religa a sessão se o proxy mudou. Vazio (`:-`) = sem proxy (sai pelo IP da máquina).

## Verificação (antes e depois de recriar)

Dois scripts ajudam a não cair nos dois erros silenciosos do proxy:

```powershell
# ANTES de recriar — recusa subir se algum chip estiver meio-preenchido
# (server sem credencial, ou credencial sem server), que quebra o chip em silêncio.
./scripts/check-proxy-env.ps1

# DEPOIS de recriar — confirma que o proxy entrou no container e mostra os logs
# de conexão. Opcional: -Chips 1,2 pra olhar só alguns.
./scripts/check-proxy-live.ps1
```

⚠️ **Não dá pra provar o IP de saída com `curl` de dentro do container.** O `WHATSAPP_PROXY_SERVER`
roteia **só o socket do WhatsApp** (interno ao engine do WAHA), não o tráfego geral do container —
um `curl` sairia pelo IP da máquina de qualquer jeito e daria um falso "está vazando". A **prova
definitiva** do IP é o **painel da Decodo**: o tráfego aparece no IP alugado quando o chip conecta.

## Ressalvas honestas

- **Localhost funciona normal.** O sistema continua local/privado; só o caminho de saída da
  conexão muda (sua máquina → proxy remoto → WhatsApp).
- **Proxy NÃO impede um chip de ser banido** por comportamento. Ele protege a **frota** do
  efeito-dominó por IP compartilhado — é a **2ª camada**.
- **A 1ª camada (a que evita ban de verdade) é o comportamento:** delays (45–75s), typing
  simulado, aquecimento e opt-out. Já implementados; mantenha ligados.
- **Engine NOWEB respeita o proxy** na conexão principal (mensagens). Há um detalhe conhecido de
  que o download de mídia pode não passar pelo proxy — não afeta disparo de texto.
- ✅ **Bypass do webhook já vem configurado.** Cada serviço `waha` dos composes traz
  `NO_PROXY=api,localhost,127.0.0.1` (e `no_proxy` minúsculo), pra o webhook `waha` →
  `http://api:8080/...` (host INTERNO do Docker) **não** sair pelo proxy externo. Sem isso, o
  "SAIR" pararia de chegar em silêncio (risco de ban). Não precisa configurar nada à mão.
- ⚠️ **Ainda assim, TESTE O OPT-OUT ao ligar um proxy.** Mande um "SAIR" de teste e confirme que o
  contato fica "Saiu". Se NÃO chegar, verifique se o `NO_PROXY` continua no serviço `waha` daquele
  ambiente e se o host do webhook (`api`) está coberto por ele.

## Referências
- WAHA — Proxy: https://waha.devlike.pro/docs/how-to/proxy/
- WAHA — Configuration: https://waha.devlike.pro/docs/how-to/config/
