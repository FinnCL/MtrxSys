#!/usr/bin/env bash
# Sobe 1 emulador Android (docker-android + noVNC) numa VM do GCP com virtualização aninhada (KVM).
# Uso pensado pro TESTE GRÁTIS do GCP ($300/90d) — valida o fluxo "provisionar número" do ambiente 1.
# Rode no Cloud Shell (console.cloud.google.com → ícone do terminal) — gcloud já vem autenticado lá.
#
#   bash gcp-emulator-up.sh                 # cria a VM e sobe o emulador
#   ZONE=us-central1-a NAME=mtrx-emu-1 bash gcp-emulator-up.sh
#
# No fim ele imprime a URL noVNC (http://IP:6080) pra você pôr em PHONE_VIEW_URL_1 no .env do dashboard.
set -euo pipefail

NAME="${NAME:-mtrx-emu-1}"
ZONE="${ZONE:-us-central1-a}"
MACHINE="${MACHINE:-n2-standard-4}"          # 4 vCPU / 16 GB — segura 1 emulador de teste com folga
IMAGE_FAMILY="${IMAGE_FAMILY:-ubuntu-2204-lts}"
IMAGE_PROJECT="${IMAGE_PROJECT:-ubuntu-os-cloud}"
ANDROID_IMAGE="${ANDROID_IMAGE:-budtmo/docker-android:emulator_14.0}"
EMULATOR_DEVICE="${EMULATOR_DEVICE:-Samsung Galaxy S10}"

# IP de origem liberado no firewall do noVNC. Por segurança, default = SÓ o seu IP atual.
MY_IP="$(curl -s https://api.ipify.org || true)"
SRC_RANGE="${SRC_RANGE:-${MY_IP:+${MY_IP}/32}}"
SRC_RANGE="${SRC_RANGE:-0.0.0.0/0}"   # fallback (aberto) se não der pra detectar o IP — restrinja depois!

echo ">> Projeto: $(gcloud config get-value project 2>/dev/null)"
echo ">> Criando VM '$NAME' em $ZONE ($MACHINE) com virtualização aninhada…"

# Startup-script que roda DENTRO da VM: instala docker e sobe o docker-android com /dev/kvm.
read -r -d '' STARTUP <<EOF || true
#!/bin/bash
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y docker.io
systemctl enable --now docker
# pré-condição do KVM (a flag --enable-nested-virtualization já entrega /dev/kvm na VM)
egrep -c '(vmx|svm)' /proc/cpuinfo || echo "AVISO: sem vmx/svm — nested virt não ativou"
docker rm -f mtrx-android 2>/dev/null || true
docker run -d --name mtrx-android --restart unless-stopped \\
  --device /dev/kvm \\
  -p 6080:6080 -p 5555:5555 \\
  -e EMULATOR_DEVICE="${EMULATOR_DEVICE}" \\
  -e WEB_VNC=true \\
  ${ANDROID_IMAGE}
EOF

gcloud compute instances create "$NAME" \
  --zone="$ZONE" \
  --machine-type="$MACHINE" \
  --enable-nested-virtualization \
  --min-cpu-platform="Intel Haswell" \
  --image-family="$IMAGE_FAMILY" \
  --image-project="$IMAGE_PROJECT" \
  --boot-disk-size=40GB \
  --metadata=startup-script="$STARTUP"

echo ">> Liberando o noVNC (tcp:6080) só pra $SRC_RANGE …"
gcloud compute firewall-rules create mtrx-emu-novnc \
  --allow=tcp:6080 --source-ranges="$SRC_RANGE" --description="noVNC do emulador mtrx" \
  2>/dev/null || echo "   (regra já existe — ok)"

IP="$(gcloud compute instances describe "$NAME" --zone="$ZONE" \
  --format='get(networkInterfaces[0].accessConfigs[0].natIP)')"

cat <<MSG

=====================================================================
 Emulador subindo. O Android leva ~2-4 min pra bootar (1ª vez baixa a imagem).
 noVNC (tela do Android):  http://${IP}:6080

 No dashboard, ponha no .env do ambiente 1 e rebuild o web:
   PHONE_VIEW_URL_1=http://${IP}:6080
   PHONE_VIEWER_KIND_1=          (vazio = embute a url direto, sem deep-link scrcpy)

 Quando terminar o teste, derrube tudo (pra não gastar crédito):
   gcloud compute instances delete ${NAME} --zone=${ZONE} -q
   gcloud compute firewall-rules delete mtrx-emu-novnc -q
=====================================================================
MSG
