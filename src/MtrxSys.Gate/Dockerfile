# Portão de autenticação (MtrxSys.Gate). Contexto de build = RAIZ do repo (igual à api).
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/MtrxSys.Gate/MtrxSys.Gate.csproj src/MtrxSys.Gate/
RUN dotnet restore src/MtrxSys.Gate/MtrxSys.Gate.csproj
COPY src/MtrxSys.Gate/ src/MtrxSys.Gate/
RUN dotnet publish src/MtrxSys.Gate/MtrxSys.Gate.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MtrxSys.Gate.dll"]
