# Changelog

## 1.0.0 (2026-07-12)


### Bug Fixes

* **ci:** otorgar permisos explicitos al job semver (contents:write, pull-requests:write) para resolver 403 de release-please ([2cfb45c](https://github.com/nestormauricio/Makers-atlas-pars/commit/2cfb45cf1e87ee2ef16045de7441fb57c4a0fb5a))
* **ci:** suprimir falso positivo CVE-2026-39883 (OpenTelemetry-Go, no aplica a paquetes .NET) con justificacion documentada ([37296f0](https://github.com/nestormauricio/Makers-atlas-pars/commit/37296f0e7cf5e3bab6503827f58aae25cbcfc37c))
* **ci:** usar formato tabla como gate real de Trivy (bug conocido del formato sarif con .NET multi-root); SARIF queda best-effort para la pestaña Security ([ca8b0af](https://github.com/nestormauricio/Makers-atlas-pars/commit/ca8b0af6eb515dccdb1b368647d0dc8c34062976))
* **healthcheck:** implementar modo --healthcheck en Program.cs ([7cea095](https://github.com/nestormauricio/Makers-atlas-pars/commit/7cea0956536b7512d13e0a77f6ebf4e05b5fd53a))
