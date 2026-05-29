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
COPY . .
RUN npm run build

FROM nginx:alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
