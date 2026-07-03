#!/usr/bin/env bash
# Firewall do MtrxSys — bloqueia acesso EXTERNO às portas sensíveis publicadas pelo Docker:
#   • 5440      → Postgres COMPARTILHADO (ledger de dedup/opt-out cross-chip)
#   • 6080-6089 → noVNC dos emuladores Android (tela do chip) — engine docker-android
#   • 5555-5564 → adb dos redroid (controle total do Android: shell/install) — engine redroid
# Mantém abertos: 22 (SSH), 80/443 (Caddy). Mantém o tráfego INTERNO do Docker (172.16.0.0/12)
# — os ambientes alcançam o ledger via host.docker.internal, então NÃO pode bloquear a rede docker.
#
# Usa a chain DOCKER-USER (a correta pra portas publicadas pelo Docker: a regra vê a porta ORIGINAL
# via conntrack, antes do DNAT) e persiste via systemd (reaplica no boot, depois do docker.service).
#
# Rode UMA vez, com sudo, da raiz do repo:  sudo bash deploy/setup-firewall.sh
set -euo pipefail
[ "$(id -u)" = "0" ] || { echo "Rode com sudo: sudo bash deploy/setup-firewall.sh"; exit 1; }

# 1) Script das regras (idempotente: -C antes de -I pra não duplicar).
cat > /usr/local/bin/mtrx-firewall.sh <<'RULES'
#!/bin/bash
# Dropa 5440 + 6080-6089 + 5555-5564 de fontes que NÃO são da rede docker (= internet), preservando
# o docker (a API alcança o adb do redroid via host-gateway, que é source da rede docker → não cai).
for spec in "5440" "6080:6089" "5555:5564"; do
  iptables -C DOCKER-USER -p tcp -m conntrack --ctorigdstport "$spec" ! -s 172.16.0.0/12 -j DROP 2>/dev/null \
    || iptables -I DOCKER-USER -p tcp -m conntrack --ctorigdstport "$spec" ! -s 172.16.0.0/12 -j DROP
done
RULES
chmod 0755 /usr/local/bin/mtrx-firewall.sh

# 2) Unit systemd — reaplica no boot DEPOIS do docker (que recria a chain DOCKER-USER).
cat > /etc/systemd/system/mtrx-firewall.service <<'UNIT'
[Unit]
Description=MtrxSys firewall (bloqueia portas sensiveis do Docker da internet)
After=docker.service
Requires=docker.service

[Service]
Type=oneshot
ExecStart=/usr/local/bin/mtrx-firewall.sh
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now mtrx-firewall.service

echo "✓ Firewall aplicado e habilitado no boot. Regras na DOCKER-USER:"
iptables -L DOCKER-USER -n --line-numbers
