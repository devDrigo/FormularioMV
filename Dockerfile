# FluigPainel Dockerfile - Corrigido para encoding
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# CONFIGURA ENCODING UTF-8 (RESOLVE O PROBLEMA DO "ç")
RUN apt-get update && apt-get install -y locales \
    && rm -rf /var/lib/apt/lists/* \
    && localedef -i en_US -c -f UTF-8 -A /usr/share/locale/locale.alias en_US.UTF-8
ENV LANG=en_US.UTF-8
ENV LC_ALL=en_US.UTF-8
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_USE_POLLING_FILE_WATCHER=1

WORKDIR /src

# Copia APENAS o .csproj primeiro (cache otimizado)
COPY ["FluigPainel.csproj", "."]
RUN dotnet restore "FluigPainel.csproj"

# Copia todo o resto
COPY . .

# Build com encoding correto
RUN dotnet build "FluigPainel.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FluigPainel.csproj" -c Release -o /app/publish

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Configura locale também no runtime
RUN apt-get update && apt-get install -y locales \
    && rm -rf /var/lib/apt/lists/* \
    && localedef -i en_US -c -f UTF-8 -A /usr/share/locale/locale.alias en_US.UTF-8
ENV LANG=en_US.UTF-8
ENV LC_ALL=en_US.UTF-8

EXPOSE 8080
ENV ASPNETCORE_URLS=http://*:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_USE_POLLING_FILE_WATCHER=1

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "FluigPainel.dll"]
