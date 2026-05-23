FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/MtrxSys.Core/MtrxSys.Core.csproj src/MtrxSys.Core/
COPY src/MtrxSys.Infrastructure/MtrxSys.Infrastructure.csproj src/MtrxSys.Infrastructure/
COPY src/MtrxSys.Cli/MtrxSys.Cli.csproj src/MtrxSys.Cli/
RUN dotnet restore src/MtrxSys.Cli/MtrxSys.Cli.csproj

COPY src/MtrxSys.Core/ src/MtrxSys.Core/
COPY src/MtrxSys.Infrastructure/ src/MtrxSys.Infrastructure/
COPY src/MtrxSys.Cli/ src/MtrxSys.Cli/
RUN dotnet publish src/MtrxSys.Cli/MtrxSys.Cli.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /work
COPY --from=build /app /opt/mtrx
ENTRYPOINT ["dotnet", "/opt/mtrx/mtrx.dll"]
