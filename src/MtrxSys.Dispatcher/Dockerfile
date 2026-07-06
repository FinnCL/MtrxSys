FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/MtrxSys.Core/MtrxSys.Core.csproj src/MtrxSys.Core/
COPY src/MtrxSys.Infrastructure/MtrxSys.Infrastructure.csproj src/MtrxSys.Infrastructure/
COPY src/MtrxSys.Dispatcher/MtrxSys.Dispatcher.csproj src/MtrxSys.Dispatcher/
RUN dotnet restore src/MtrxSys.Dispatcher/MtrxSys.Dispatcher.csproj

COPY src/MtrxSys.Core/ src/MtrxSys.Core/
COPY src/MtrxSys.Infrastructure/ src/MtrxSys.Infrastructure/
COPY src/MtrxSys.Dispatcher/ src/MtrxSys.Dispatcher/
RUN dotnet publish src/MtrxSys.Dispatcher/MtrxSys.Dispatcher.csproj -c Release -o /app /p:UseAppHost=false

# Estágio de DEV: hot-reload via `dotnet watch`. Usado só pelo docker-compose.dev.yml (bind-mount
# do código por cima). NÃO é o estágio final — o build de produção/CI segue buildando o runtime.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS dev
WORKDIR /src
ENV DOTNET_USE_POLLING_FILE_WATCHER=true \
    DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true
# Restaura na imagem (1º start rápido); volumes anônimos de obj/bin no compose preservam o restore
# e impedem o bin/obj do host (Windows) de contaminar o container.
COPY Directory.Build.props ./
COPY src/MtrxSys.Core/MtrxSys.Core.csproj src/MtrxSys.Core/
COPY src/MtrxSys.Infrastructure/MtrxSys.Infrastructure.csproj src/MtrxSys.Infrastructure/
COPY src/MtrxSys.Dispatcher/MtrxSys.Dispatcher.csproj src/MtrxSys.Dispatcher/
RUN dotnet restore src/MtrxSys.Dispatcher/MtrxSys.Dispatcher.csproj
CMD ["dotnet", "watch", "--project", "src/MtrxSys.Dispatcher/MtrxSys.Dispatcher.csproj", "run"]

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
# docker-cli: o DispatchEngine grava o contato na agenda do emulador ANTES de cada envio (perfil
# menos-robô/anti-ban) via `docker exec <emulador> adb ...` sobre o socket montado. Inócuo onde o
# socket não é montado — o SaveContactAsync é best-effort e degrada limpo.
RUN apk add --no-cache docker-cli
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "MtrxSys.Dispatcher.dll"]
