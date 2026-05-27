FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/MtrxSys.Core/MtrxSys.Core.csproj src/MtrxSys.Core/
COPY src/MtrxSys.Infrastructure/MtrxSys.Infrastructure.csproj src/MtrxSys.Infrastructure/
COPY src/MtrxSys.Api/MtrxSys.Api.csproj src/MtrxSys.Api/
RUN dotnet restore src/MtrxSys.Api/MtrxSys.Api.csproj

COPY src/MtrxSys.Core/ src/MtrxSys.Core/
COPY src/MtrxSys.Infrastructure/ src/MtrxSys.Infrastructure/
COPY src/MtrxSys.Api/ src/MtrxSys.Api/
RUN dotnet publish src/MtrxSys.Api/MtrxSys.Api.csproj -c Release -o /app /p:UseAppHost=false

# Estágio de DEV: hot-reload via `dotnet watch`. Usado só pelo docker-compose.dev.yml, que faz
# bind-mount do código por cima. NÃO é o estágio final (runtime segue por último), então o build
# de produção/CI — que builda o último estágio — não é afetado.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS dev
WORKDIR /src
ENV DOTNET_USE_POLLING_FILE_WATCHER=true \
    DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true \
    ASPNETCORE_URLS=http://+:8080
# Restaura na imagem pra o 1º start ser rápido; os volumes anônimos de obj/bin (no compose)
# preservam esse restore e impedem o bin/obj do host (Windows) de contaminar o container.
COPY Directory.Build.props ./
COPY src/MtrxSys.Core/MtrxSys.Core.csproj src/MtrxSys.Core/
COPY src/MtrxSys.Infrastructure/MtrxSys.Infrastructure.csproj src/MtrxSys.Infrastructure/
COPY src/MtrxSys.Api/MtrxSys.Api.csproj src/MtrxSys.Api/
RUN dotnet restore src/MtrxSys.Api/MtrxSys.Api.csproj
EXPOSE 8080
CMD ["dotnet", "watch", "--project", "src/MtrxSys.Api/MtrxSys.Api.csproj", "run", "--no-launch-profile"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MtrxSys.Api.dll"]
