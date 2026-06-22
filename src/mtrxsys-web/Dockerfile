FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

FROM deps AS dev
ENV CHOKIDAR_USEPOLLING=true
ENV WATCHPACK_POLLING=true
EXPOSE 5173
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]

FROM deps AS build
# VITE_API_URL é build-time (Vite injeta no bundle final). Default mantém comportamento
# do Stack 1 (localhost:5080); Stack 2 sobrescreve via docker compose build.args.
ARG VITE_API_URL=http://localhost:5080
ENV VITE_API_URL=$VITE_API_URL
# VITE_APP_LABEL diferencia a aba do navegador entre ambientes ("A", "B", ...). Default
# vazio = titulo fica "MtrxSys" puro (comportamento Stack 1 sem multi-ambiente).
ARG VITE_APP_LABEL=
ENV VITE_APP_LABEL=$VITE_APP_LABEL
# VITE_EMULATOR_URL = URL da tela embutível na aba "Celular" (maquete / ws-scrcpy do Android local /
# noVNC). Preenchido nos ambientes A/B.
ARG VITE_EMULATOR_URL=
ENV VITE_EMULATOR_URL=$VITE_EMULATOR_URL
# VITE_EMULATOR_KIND = "scrcpy" quando a tela embutida é o ws-scrcpy (monta o stream direto, pula a
# lista de devices). Vazio = embute a VITE_EMULATOR_URL como está (maquete/noVNC).
ARG VITE_EMULATOR_KIND=
ENV VITE_EMULATOR_KIND=$VITE_EMULATOR_KIND
# VITE_PHONE_SERVER = "1" mostra a seção "Android em container (KVM)" na aba Celular (só no servidor
# Linux com KVM). Vazio (padrão) = oculta no localhost.
ARG VITE_PHONE_SERVER=
ENV VITE_PHONE_SERVER=$VITE_PHONE_SERVER
# VITE_EMULATOR_UDID = device adb a espelhar (LDPlayer: emulator-5554/5556…). Vazio = emulator-5554.
ARG VITE_EMULATOR_UDID=
ENV VITE_EMULATOR_UDID=$VITE_EMULATOR_UDID
COPY . .
RUN npm run build

FROM nginx:alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
# Config que NÃO cacheia o index.html (pega deploy novo sem hard-refresh) e cacheia os assets com hash.
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
