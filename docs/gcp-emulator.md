# Emulador no GCP (teste grátis) — fechar o fluxo do ambiente 1

Objetivo: rodar **1 emulador Android real** (docker-android + noVNC) num host com **KVM** pra validar, de
ponta a ponta, o botão **"Provisionar número"** da aba "Celular": *ligar → bootar → instalar WhatsApp →
proxy → registrar por SMS → vincular o WAHA*. Local no Windows não roda (sem KVM nativo); o GCP free trial
tem **virtualização aninhada**.

> Escopo: o free trial segura **1 emulador de teste**, não os 10. Os 10 são pro servidor dedicado de
> produção — ver [scaling.md](./scaling.md).

## Passo a passo (sem instalar nada — via Cloud Shell)

1. Abra o **Console** do GCP e ative o **teste grátis** ($300/90 dias): https://console.cloud.google.com
2. Clique no ícone **Cloud Shell** (`>_`, canto superior direito). Abre um terminal Linux no navegador,
   **já autenticado** — não precisa instalar o `gcloud`.
3. Mande o script do repositório pro Cloud Shell (arraste o arquivo `scripts/gcp-emulator-up.sh` pra
   janela do Cloud Shell, ou cole o conteúdo num arquivo) e rode:
   ```bash
   bash gcp-emulator-up.sh
   ```
   Ele cria a VM (`n2-standard-4`, **nested virt** ligada), sobe o `docker-android` com `/dev/kvm`,
   libera o `tcp:6080` **só pro seu IP**, e imprime a **URL do noVNC**.
4. Espere ~2-4 min (1º boot baixa a imagem) e abra a `http://IP:6080` — você vê o **Android ao vivo**.

## Plugar no dashboard

No `.env` do ambiente 1 (ou nas envs do compose), aponte a aba "Celular" pro emulador e rebuild o web:

```env
PHONE_VIEW_URL_1=http://SEU_IP_GCP:6080
PHONE_VIEWER_KIND_1=
```
```bash
docker compose -p mtrxsys -f docker-compose.yml up -d --no-deps --build web
```

Agora a aba "Celular" embute a tela do Android (no lugar do QR/maquete). Em **"Mostrar opção de servidor
→ Provisionar número"**, o checklist roda os passos automáticos e te guia no SMS e no QR do WAHA — que é
lido **por dentro do emulador**, exatamente como no servidor de produção.

> O botão "Provisionar número" controla o Android pelo **docker.sock** local. Pra ele agir sobre o
> emulador do GCP, rode o dashboard **na própria VM** (ou exponha o docker remoto). Pro 1º teste é mais
> simples **só embutir a tela** (PHONE_VIEW_URL_1) e fazer install/registro pela tela do noVNC.

## Encerrar (não gastar crédito)

```bash
gcloud compute instances delete mtrx-emu-1 --zone=us-central1-a -q
gcloud compute firewall-rules delete mtrx-emu-novnc -q
```

## Notas

- **Custo:** `n2-standard-4` ≈ US$0,19/h. Desligue (`instances stop`) quando não usar; delete no fim.
- **Segurança:** o script libera o noVNC só pro seu IP (`/32`). Se cair no fallback `0.0.0.0/0`,
  restrinja depois — noVNC aberto é um Android exposto na internet.
- **Por que `n2` e não `e2`:** a família `e2` **não** suporta virtualização aninhada; `n2` + min CPU
  `Intel Haswell` suporta. Sem isso o emulador não boota (QEMU sem KVM = travado).
