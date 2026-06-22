# Emulador do WAHA (drop-in p/ DEV/teste). Substitui o container `waha` em A/B sem mudar o app:
# fala a mesma API HTTP e escuta na MESMA porta interna (3000), então Waha__BaseUrl segue igual.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/MtrxSys.WahaEmulator/MtrxSys.WahaEmulator.csproj src/MtrxSys.WahaEmulator/
RUN dotnet restore src/MtrxSys.WahaEmulator/MtrxSys.WahaEmulator.csproj

COPY src/MtrxSys.WahaEmulator/ src/MtrxSys.WahaEmulator/
RUN dotnet publish src/MtrxSys.WahaEmulator/MtrxSys.WahaEmulator.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
# Porta 3000 = mesma do WAHA real (o app aponta Waha__BaseUrl=http://waha:3000).
ENV ASPNETCORE_URLS=http://+:3000
EXPOSE 3000
ENTRYPOINT ["dotnet", "MtrxSys.WahaEmulator.dll"]
