# Recuperação após ban

O que fazer quando um número é banido. Resumo: **não basta trocar o chip** — o **aparelho também
queima**.

## O que é banido
- O WhatsApp bane a **CONTA (número)** — não o IP.
- O **aparelho (emulador)** fica **flagueado**: registrar um número novo **no mesmo emulador** bane
  rápido (ele lembra da digital).

## Passos pra recuperar
```
1. Descarta o CHIP banido  → chip NOVO (número novo)
2. Aparelho LIMPO          → nova instância do emulador  OU  randomizar a digital (IMEI/Android ID)
3. (Recomendado) PROXY novo → IP sem histórico
4. Reinstala o WhatsApp → registra → reparea o WAHA do ambiente (QR na aba "Celular")
```

## ⚠️ Factory reset NÃO basta
Um "restaurar padrão de fábrica" do Android **não muda** a digital (IMEI/Android ID) que o emulador
apresenta. Tem que ser:
- **Nova instância** do emulador (LDMultiPlayer "Novo" / novo container docker-android), **ou**
- **Randomizar a info do aparelho** (no LDPlayer: Configurações → device info → gerar IMEI/Android ID
  novos).

## Por que a regra 1:1:1:1 protege
Com **1 número : 1 emulador : 1 proxy : 1 ambiente**, um ban atinge **só aquele** número/emulador. Você
descarta **só aquele chip + emulador** e os **outros 9 ambientes seguem intactos** — porque cada conta
vive numa digital separada (sem pista de que são da mesma operação).

> Se você **empilhar** (2 contas num emulador), um ban **arrasta as duas** — a digital é a mesma. Ver
> **[architecture.md](architecture.md)**.

## Prevenção (anti-ban)
- **1 número por emulador** (digital isolada).
- **Proxy por ambiente** (`WAHA_PROXY_N`) — IP isolado.
- **Aquecimento** do chip (já no sistema) e **número descartável** em emulador.
- Não reusar IP/aparelho de conta banida.
