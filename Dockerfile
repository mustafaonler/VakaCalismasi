# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first so the layer is cached until a project file changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/AtmCash.Api/AtmCash.Api.csproj src/AtmCash.Api/
COPY src/AtmCash.Application/AtmCash.Application.csproj src/AtmCash.Application/
COPY src/AtmCash.Domain/AtmCash.Domain.csproj src/AtmCash.Domain/
COPY src/AtmCash.Infrastructure/AtmCash.Infrastructure.csproj src/AtmCash.Infrastructure/
RUN dotnet restore src/AtmCash.Api/AtmCash.Api.csproj

COPY src/ src/
RUN dotnet publish src/AtmCash.Api/AtmCash.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AtmCash.Api.dll"]
