# Use uma imagem oficial do .NET 6 como base
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80

# Use uma imagem oficial do SDK do .NET 6 para build
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["FormularioMV/FormularioMV.csproj", "FormularioMV/"]
RUN dotnet restore "FormularioMV/FormularioMV.csproj"
COPY . .
WORKDIR "/src/FormularioMV"
RUN dotnet build "FormularioMV.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FormularioMV.csproj" -c Release -o /app/publish

# Copie os arquivos publicados para a imagem base
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "FormularioMV.dll"]
