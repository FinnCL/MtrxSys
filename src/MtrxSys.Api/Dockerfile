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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MtrxSys.Api.dll"]
