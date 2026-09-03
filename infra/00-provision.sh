#!/usr/bin/env bash
# FAZ 0 — Azure altyapısını kurar. Idempotent: tekrar çalıştırmak güvenlidir,
# var olan kaynağa dokunmaz.
#
# Kurduğu şeyler:
#   resource group, ACR, Log Analytics workspace, Container Apps environment,
#   user-assigned managed identity + rol atamaları (AcrPull, Azure OpenAI User)
#
# Container App'in ve Job'ın KENDİSİNİ kurmaz — onlar Faz 1/Faz 2'de, ortada
# gerçek bir image varken oluşturulacak.
set -euo pipefail

cd "$(dirname "$0")"
[ -f .env ] || { echo "HATA: infra/.env yok. 'cp infra/.env.example infra/.env' ile başlayın." >&2; exit 1; }
# shellcheck disable=SC1091
set -a; . ./.env; set +a

command -v az >/dev/null || { echo "HATA: azure-cli kurulu değil (brew install azure-cli)." >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "HATA: 'az login' yapılmamış." >&2; exit 1; }

[ -n "${AZURE_SUBSCRIPTION:-}" ] && az account set --subscription "$AZURE_SUBSCRIPTION"
SUB_NAME=$(az account show --query name -o tsv)
echo "==> Abonelik: $SUB_NAME"

echo "==> containerapp uzantısı ve resource provider'lar"
az extension add --name containerapp --upgrade --only-show-errors >/dev/null
# Bu üçü kayıtlı değilse ACA environment yaratma anlaşılmaz bir hatayla patlar.
for ns in Microsoft.App Microsoft.OperationalInsights Microsoft.ContainerRegistry; do
    az provider register --namespace "$ns" --wait --only-show-errors
done

echo "==> Resource group: $RG"
az group create -n "$RG" -l "$LOCATION" --only-show-errors >/dev/null

echo "==> Container Registry: $ACR_NAME"
az acr show -n "$ACR_NAME" -g "$RG" >/dev/null 2>&1 \
    || az acr create -g "$RG" -n "$ACR_NAME" --sku Basic --only-show-errors >/dev/null
ACR_ID=$(az acr show -n "$ACR_NAME" -g "$RG" --query id -o tsv)
ACR_SERVER=$(az acr show -n "$ACR_NAME" -g "$RG" --query loginServer -o tsv)

echo "==> Managed identity: $IDENTITY_NAME"
az identity show -g "$RG" -n "$IDENTITY_NAME" >/dev/null 2>&1 \
    || az identity create -g "$RG" -n "$IDENTITY_NAME" --only-show-errors >/dev/null
IDENTITY_ID=$(az identity show -g "$RG" -n "$IDENTITY_NAME" --query id -o tsv)
IDENTITY_PRINCIPAL=$(az identity show -g "$RG" -n "$IDENTITY_NAME" --query principalId -o tsv)
IDENTITY_CLIENT=$(az identity show -g "$RG" -n "$IDENTITY_NAME" --query clientId -o tsv)

echo "==> Log Analytics: $LAW_NAME"
az monitor log-analytics workspace show -g "$RG" -n "$LAW_NAME" >/dev/null 2>&1 \
    || az monitor log-analytics workspace create -g "$RG" -n "$LAW_NAME" -l "$LOCATION" --only-show-errors >/dev/null
LAW_CUSTOMER_ID=$(az monitor log-analytics workspace show -g "$RG" -n "$LAW_NAME" --query customerId -o tsv)
LAW_KEY=$(az monitor log-analytics workspace get-shared-keys -g "$RG" -n "$LAW_NAME" --query primarySharedKey -o tsv)

echo "==> Container Apps environment: $ACA_ENV"
az containerapp env show -g "$RG" -n "$ACA_ENV" >/dev/null 2>&1 \
    || az containerapp env create -g "$RG" -n "$ACA_ENV" -l "$LOCATION" \
         --logs-workspace-id "$LAW_CUSTOMER_ID" --logs-workspace-key "$LAW_KEY" --only-show-errors >/dev/null

# --- Rol atamaları -----------------------------------------------------------
# Rol ataması yayılması birkaç dakika sürebilir; hemen ardından deploy denerseniz
# ilk deneme "unauthorized" alabilir, bu normaldir.
assign_role () {  # $1=rol adı  $2=scope
    if az role assignment list --assignee "$IDENTITY_PRINCIPAL" --scope "$2" \
         --query "[?roleDefinitionName=='$1']" -o tsv | grep -q .; then
        echo "    zaten var: $1"
    else
        az role assignment create \
            --assignee-object-id "$IDENTITY_PRINCIPAL" \
            --assignee-principal-type ServicePrincipal \
            --role "$1" --scope "$2" --only-show-errors >/dev/null
        echo "    atandı: $1"
    fi
}

echo "==> Rol atamaları"
assign_role "AcrPull" "$ACR_ID"

if [ -n "${AOAI_RESOURCE_ID:-}" ]; then
    assign_role "Cognitive Services OpenAI User" "$AOAI_RESOURCE_ID"
else
    echo "    ATLANDI: Cognitive Services OpenAI User (AOAI_RESOURCE_ID boş)"
    echo "             Faz 3'te managed identity'ye geçmeden önce doldurulmalı."
fi

cat <<SUMMARY

================= FAZ 0 ALTYAPI HAZIR =================
  Resource group      : $RG ($LOCATION)
  ACR login server    : $ACR_SERVER
  ACA environment     : $ACA_ENV
  Managed identity    : $IDENTITY_NAME
    resource id       : $IDENTITY_ID
    client id         : $IDENTITY_CLIENT   <- Faz 3'te AZURE_CLIENT_ID olarak lazım
  Log Analytics       : $LAW_NAME

Sıradaki adım:
  ./10-build-push.sh v0.0.1
=======================================================
SUMMARY
