# Diseño de despliegue en Azure para Atlas PARS (ver docs/ARCHITECTURE.md y ADR-0003).
# No se ejecuta desde este sandbox (sin salida a registry.terraform.io), pero es Terraform
# válido y ejecutable en cualquier máquina con `terraform init && terraform plan`.
#
# Decisiones clave:
# - Container Apps (no AKS): el servicio es stateless y multi-tenant por convención de
#   aplicación (no por namespace), así que no necesitamos la complejidad operativa de un
#   clúster completo para un solo servicio de plataforma. Si Atlas crece a una familia de
#   servicios, migrar a AKS es la evolución natural (ver ADR-0003).
# - Key Vault con Managed Identity: cero secretos en variables de entorno de la plataforma
#   de cómputo; el contenedor obtiene la llave de firma vía identidad administrada.
# - Multi-tenant vía particionamiento lógico (un container app, políticas y auditoría
#   particionadas por tenantId) en vez de un container app por squad: más barato y más simple
#   de operar a la escala esperada; el aislamiento fuerte de datos se logra a nivel de
#   aplicación (ver ADR-0003) y puede endurecerse a aislamiento físico por tenant si algún
#   squad de alto riesgo lo requiere.

terraform {
  required_version = ">= 1.7"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.100"
    }
  }
}

provider "azurerm" {
  features {}
}

variable "environment" {
  description = "dev | staging | prod"
  type        = string
  default     = "dev"
}

variable "location" {
  type    = string
  default = "eastus2"
}

variable "container_image" {
  description = "Imagen publicada por el pipeline de CI/CD, ej. ghcr.io/org/atlas-pars:sha-abc123"
  type        = string
}

locals {
  name_prefix = "atlas-pars-${var.environment}"
  tags = {
    project     = "atlas-pars"
    environment = var.environment
    owner       = "platform-squad"
  }
}

resource "azurerm_resource_group" "this" {
  name     = "rg-${local.name_prefix}"
  location = var.location
  tags     = local.tags
}

resource "azurerm_log_analytics_workspace" "this" {
  name                = "law-${local.name_prefix}"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_container_app_environment" "this" {
  name                       = "cae-${local.name_prefix}"
  resource_group_name        = azurerm_resource_group.this.name
  location                   = azurerm_resource_group.this.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id
  tags                       = local.tags
}

# --- Key Vault: llaves de firma, con acceso solo vía identidad administrada del container app ---
resource "azurerm_key_vault" "this" {
  name                       = "kv-${substr(local.name_prefix, 0, 20)}"
  resource_group_name        = azurerm_resource_group.this.name
  location                   = azurerm_resource_group.this.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  purge_protection_enabled   = true
  soft_delete_retention_days = 90
  tags                       = local.tags
}

data "azurerm_client_config" "current" {}

resource "azurerm_user_assigned_identity" "app" {
  name                = "id-${local.name_prefix}"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.tags
}

resource "azurerm_key_vault_access_policy" "app_read" {
  key_vault_id = azurerm_key_vault.this.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = azurerm_user_assigned_identity.app.principal_id

  secret_permissions = ["Get", "List"]
  key_permissions     = ["Get", "List", "Sign", "Verify"]
}

# Placeholder declarativo: el VALOR real de la llave nunca se gestiona con Terraform (ni se
# commitea). Se rota mediante `az keyvault secret set` desde el pipeline de CD o manualmente
# (ver docs/RUNBOOK.md "Rotación de llaves"). `ignore_changes` evita que `terraform apply`
# sobreescriba una rotación real con este valor de placeholder.
resource "azurerm_key_vault_secret" "signing_key" {
  name         = "atlas-signing-key"
  key_vault_id = azurerm_key_vault.this.id
  value        = "REEMPLAZAR-VIA-CD-NUNCA-COMMITEAR-VALOR-REAL"
  tags         = local.tags

  lifecycle {
    ignore_changes = [value]
  }
}

# --- Container App: el servicio en sí ---
resource "azurerm_container_app" "api" {
  name                         = "ca-${local.name_prefix}"
  resource_group_name          = azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  template {
    min_replicas = var.environment == "prod" ? 3 : 1
    max_replicas = var.environment == "prod" ? 20 : 3

    container {
      name   = "atlas-pars-api"
      image  = var.container_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "Atlas__PoliciesDirectory"
        value = "/app/policies"
      }
      env {
        name  = "Atlas__KeyProvider"
        value = "AzureKeyVault" # activa AzureKeyVaultKeyProvider (ver ADR-0004); nunca EnvVar en prod
      }
      env {
        name  = "Atlas__KeyVaultUri"
        value = azurerm_key_vault.this.vault_uri
      }
      env {
        name  = "Atlas__SigningKeySecretName"
        value = "atlas-signing-key" # secreto gestionado fuera de Terraform, ver docs/RUNBOOK.md
      }
      env {
        name        = "AZURE_CLIENT_ID"
        value       = azurerm_user_assigned_identity.app.client_id
      }

      liveness_probe {
        transport = "HTTP"
        path      = "/health"
        port      = 8080
      }
      readiness_probe {
        transport = "HTTP"
        path      = "/health"
        port      = 8080
      }
    }

    http_scale_rule {
      name                = "http-scale"
      concurrent_requests = 50
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    traffic_weight {
      percentage      = 100
      latest_revision = true
    }
  }
}

output "container_app_fqdn" {
  value = azurerm_container_app.api.ingress[0].fqdn
}

output "key_vault_uri" {
  value = azurerm_key_vault.this.vault_uri
}
