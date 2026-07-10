# Estimación de costos (Azure, región eastus2, orden de magnitud mensual)

Estimación aproximada para el ambiente `dev` (1 réplica) y `prod` (3-20 réplicas autoescalando),
calculada con la calculadora de precios de Azure a fecha de escritura. Son órdenes de magnitud
para conversación de presupuesto, no una cotización.

| Recurso | Dev (aprox/mes) | Prod (aprox/mes, 3 réplicas base) |
|---|---|---|
| Azure Container Apps (0.5 vCPU / 1GiB, consumo) | ~US$15–25 | ~US$120–400 (según tráfico y autoescalado hasta 20) |
| Azure Key Vault (standard, operaciones de firma) | ~US$3–5 | ~US$15–30 |
| Log Analytics Workspace (30 días retención) | ~US$10–20 | ~US$50–150 (según volumen de logs) |
| Total aproximado | **~US$30–50/mes** | **~US$185–580/mes** |

Supuestos: sin egress significativo, sin Private Link (se puede añadir por ~US$8/mes por endpoint
si el squad lo requiere), sin réplica geográfica. La partida más variable es Log Analytics según
el nivel de detalle de logging que se configure — mitigable con sampling en producción.
