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

2. Recrie o WAHA do ambiente correspondente:

   ```powershell
   docker compose -f docker-compose.yml   up -d --force-recreate waha   # ambiente 1
   docker compose -f docker-compose-2.yml up -d --force-recreate waha   # ambiente 2
   # ...
   ```

3. O chip passa a sair pelo IP do proxy. Deixar a variável vazia volta ao normal (sem proxy).

### Como funciona por baixo
Cada `docker-compose-N.yml` tem, no serviço `waha`:

```yaml
WHATSAPP_PROXY_SERVER: ${WAHA_PROXY_N:-}
WHATSAPP_PROXY_SERVER_USERNAME: ${WAHA_PROXY_N_USER:-}
WHATSAPP_PROXY_SERVER_PASSWORD: ${WAHA_PROXY_N_PASS:-}
```

Vazio (`:-`) = sem proxy. O WAHA trata `WHATSAPP_PROXY_SERVER=""` como "sem proxy" (verificado:
o container sobe normal, em `SCAN_QR_CODE`, sem erro).

## Ressalvas honestas

- **Localhost funciona normal.** O sistema continua local/privado; só o caminho de saída da
  conexão muda (sua máquina → proxy remoto → WhatsApp).
- **Proxy NÃO impede um chip de ser banido** por comportamento. Ele protege a **frota** do
  efeito-dominó por IP compartilhado — é a **2ª camada**.
- **A 1ª camada (a que evita ban de verdade) é o comportamento:** delays (45–75s), typing
  simulado, aquecimento e opt-out. Já implementados; mantenha ligados.
- **Engine NOWEB respeita o proxy** na conexão principal (mensagens). Há um detalhe conhecido de
  que o download de mídia pode não passar pelo proxy — não afeta disparo de texto.
- ⚠️ **TESTE O OPT-OUT AO LIGAR UM PROXY.** O webhook é entregue de `waha` → `http://api:8080/...`
  (endereço INTERNO do Docker). Se o WAHA rotear o webhook pelo proxy externo, ele não alcança o
  host interno `api` → o "SAIR" para de chegar **silenciosamente** (risco de ban). Após ativar um
  proxy, mande um "SAIR" de teste e confirme que o contato fica "Saiu". Se NÃO chegar, configure
  um bypass de proxy pro host interno (ex.: variável `NO_PROXY=api` no serviço `waha`) ou ajuste a
  entrega do webhook pra não passar pelo proxy.

## Referências
- WAHA — Proxy: https://waha.devlike.pro/docs/how-to/proxy/
- WAHA — Configuration: https://waha.devlike.pro/docs/how-to/config/
