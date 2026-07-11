# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/AtlasPars.Api/AtlasPars.Api.csproj
RUN dotnet publish src/AtlasPars.Api/AtlasPars.Api.csproj -c Release -o /app --no-restore
# La imagen runtime (chiseled) no tiene shell, así que no se puede hacer `mkdir`/`chown` ahí.
# Se crea el directorio de auditoría aquí, en el stage de build (que sí tiene shell), y se
# copia con el propietario correcto a la imagen final.
RUN mkdir -p /audit-seed && chown 64198:64198 /audit-seed

# ---- runtime ----
# "-chiseled": imagen mínima (sin shell, sin gestor de paquetes, sin utilidades de sistema como
# perl/tar/gzip/ncurses/util-linux). Elegida deliberadamente para minimizar CVEs de superficie de
# SO que no tienen nada que ver con la app (ver hallazgo real en Trivy, CI #19): la variante
# Debian completa traía ~20 vulnerabilidades CRITICAL/HIGH de paquetes que la app nunca usa en
# runtime, solo para poder correr `curl`/`wget` en el HEALTHCHECK. Ver el modo `--healthcheck`
# en Program.cs para cómo se resuelve el healthcheck SIN esas herramientas.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

# La imagen chiseled ya corre como usuario no-root (uid 64198) por defecto — no hace falta
# crear un usuario adicional (de hecho no se puede: no hay `useradd`, no hay shell).
COPY --from=build --chown=64198:64198 /app .
COPY --chown=64198:64198 policies /app/policies
COPY --from=build --chown=64198:64198 /audit-seed /data/audit

# El volumen de auditoría se crea con el propietario correcto en tiempo de build, ya que no hay
# forma de correr `mkdir`/`chown` dentro de la imagen chiseled en runtime.
USER 64198

ENV ASPNETCORE_URLS=http://+:8080
ENV Atlas__PoliciesDirectory=/app/policies
ENV Atlas__AuditDirectory=/data/audit
VOLUME ["/data/audit"]

EXPOSE 8080

# Ver Program.cs: reutiliza el propio binario .NET ya publicado en vez de depender de
# curl/wget, que no existen en esta imagen a propósito.
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "AtlasPars.Api.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "AtlasPars.Api.dll"]
