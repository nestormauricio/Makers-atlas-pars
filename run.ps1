# Despliegue local con un solo comando (equivalente Windows de run.sh).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path ".env")) {
    Write-Host "No existe .env - copiando desde .env.example (llave de desarrollo, no usar en prod)."
    Copy-Item ".env.example" ".env"
}

Get-Content ".env" | ForEach-Object {
    if ($_ -match '^\s*#') { return }
    if ($_ -match '^\s*$') { return }
    $parts = $_.Split('=', 2)
    if ($parts.Length -eq 2) {
        [System.Environment]::SetEnvironmentVariable($parts[0].Trim(), $parts[1].Trim())
    }
}

$hasDocker = Get-Command docker -ErrorAction SilentlyContinue
if ($hasDocker) {
    docker compose -f deploy/docker-compose.yml up --build
} else {
    Write-Host "Docker no esta instalado. Corriendo la API directamente con 'dotnet run'..."
    dotnet run --project src/AtlasPars.Api
}
