# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/AtlasPars.Api/AtlasPars.Api.csproj
RUN dotnet publish src/AtlasPars.Api/AtlasPars.Api.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Principio de menor privilegio: usuario no-root dedicado (ver THREAT-MODEL.md).
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin atlas

# El volumen de auditoría debe existir y pertenecer a 'atlas' ANTES de declararlo como VOLUME:
# si no, Docker lo monta con dueño root y JsonlAuditStore (que corre como 'atlas' no-root)
# falla con UnauthorizedAccessException al primer intento de escritura.
RUN mkdir -p /data/audit && chown -R atlas:atlas /data/audit

USER atlas

COPY --from=build --chown=atlas:atlas /app .
COPY --chown=atlas:atlas policies /app/policies

ENV ASPNETCORE_URLS=http://+:8080
ENV Atlas__PoliciesDirectory=/app/policies
ENV Atlas__AuditDirectory=/data/audit
VOLUME ["/data/audit"]

EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "AtlasPars.Api.dll"]
