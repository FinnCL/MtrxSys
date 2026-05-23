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

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "MtrxSys.Dispatcher.dll"]
