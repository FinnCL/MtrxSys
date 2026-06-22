# ws-scrcpy em container: espelha a tela do emulador Android (que roda no HOST, com Play Store) para
# o navegador, pra ser EMBUTIDA na aba "Celular" do dashboard (iframe). Build interno com Node 18 (o
# ws-scrcpy nao compila em Node novo). Fala com o adb do host via ADB_SERVER_SOCKET (host precisa ter
# exposto o adb: scripts\phone-local.ps1). So DEV/localhost (Windows com emulador no host).
FROM node:18-bullseye

RUN apt-get update \
    && apt-get install -y --no-install-recommends git android-tools-adb ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
RUN git clone --depth 1 https://github.com/NetrisTV/ws-scrcpy.git .

ENV NODE_OPTIONS=--openssl-legacy-provider
RUN npm install

# Client adb deste container aponta pro servidor adb do HOST (exposto via `adb -a -P 5037`).
ENV ADB_SERVER_SOCKET=tcp:host.docker.internal:5037
EXPOSE 8000
CMD ["npm", "start"]
