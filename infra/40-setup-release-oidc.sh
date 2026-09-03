#!/usr/bin/env bash
# FAZ 3 — release.yml'in Azure'a PAROLASIZ bağlanmasını kurar (OIDC / federated
# credential) ve gereken rolleri atar. Idempotent.
#
# Neden parola değil OIDC?
#   Alternatif, bir service principal parolasını GitHub secret'ı olarak saklamaktı.
#   O parola: bir yerde durur, kopyalanır, eskir ve sızabilir — yani bu projede
#   Azure OpenAI anahtarıyla yaşanan hatanın aynısına açık. OIDC'de saklanan bir
#   sır YOK: GitHub her çalıştırmada kısa ömürlü bir token üretiyor, Azure da
#   "şu repodan, şu koşulda gelen token'a güven" diyor.
set -euo pipefail

cd "$(dirname "$0")"
[ -f .env ] || { echo "HATA: infra/.env yok." >&2; exit 1; }
# shellcheck disable=SC1091
set -a; . ./.env; set +a

GITHUB_REPO="${CI_AGENT_GITHUB_REPO:-hakancebe/ci-agent}"
APP_NAME="${RELEASE_APP_NAME:-ci-agent-release}"
WEB_APP_NAME="${WEB_APP_NAME:-ci-agent-web}"
FIX_JOB_NAME="${FIX_JOB_NAME:-ci-agent-fix}"

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)

echo "==> Uygulama kaydı: $APP_NAME"
APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)

if [ -z "$APP_ID" ] || [ "$APP_ID" = "null" ]; then
    APP_ID=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv)
    echo "    oluşturuldu: $APP_ID"
else
    echo "    zaten var: $APP_ID"
fi

# Service principal, rol atamalarının bağlanacağı nesne. Uygulama kaydı tek
# başına yetmiyor: roller SP'ye atanıyor.
SP_ID=$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv 2>/dev/null || true)
if [ -z "$SP_ID" ] || [ "$SP_ID" = "null" ]; then
    SP_ID=$(az ad sp create --id "$APP_ID" --query id -o tsv)
    echo "    service principal oluşturuldu"
fi

# --- Federated credential'lar ------------------------------------------------
# Her biri "hangi repodan, hangi koşulda gelen token'a güvenilecek" diyor.
# Etiket ve workflow_dispatch için AYRI kayıt gerekiyor: subject'ler farklı.
add_federated_credential () {  # $1=ad  $2=subject
    if az ad app federated-credential list --id "$APP_ID" \
         --query "[?name=='$1']" -o tsv 2>/dev/null | grep -q .; then
        echo "    zaten var: $1"
        return
    fi

    az ad app federated-credential create --id "$APP_ID" --parameters "{
        \"name\": \"$1\",
        \"issuer\": \"https://token.actions.githubusercontent.com\",
        \"subject\": \"$2\",
        \"audiences\": [\"api://AzureADTokenExchange\"]
    }" -o none
    echo "    eklendi: $1"
}

echo "==> Federated credential'lar ($GITHUB_REPO)"
# Etiketle tetiklenen sürümler.
add_federated_credential "release-tags" "repo:${GITHUB_REPO}:ref:refs/tags/*"
# Elle tetikleme (workflow_dispatch) main dalından koşuyor.
add_federated_credential "release-main" "repo:${GITHUB_REPO}:ref:refs/heads/main"

# --- Rol atamaları -----------------------------------------------------------
# Abonelik geneli Contributor VERMİYORUZ: bu kimlik yalnızca image push edip
# iki kaynağı güncelleyebilmeli. Dar kapsam, sızma durumunda hasarı sınırlıyor.
assign_role () {  # $1=rol  $2=scope
    if az role assignment list --assignee "$SP_ID" --scope "$2" \
         --query "[?roleDefinitionName=='$1']" -o tsv 2>/dev/null | grep -q .; then
        echo "    zaten var: $1"
    else
        az role assignment create --assignee-object-id "$SP_ID" \
            --assignee-principal-type ServicePrincipal \
            --role "$1" --scope "$2" -o none
        echo "    atandı: $1"
    fi
}

ACR_ID=$(az acr show -n "$ACR_NAME" -g "$RG" --query id -o tsv)
WEB_ID=$(az containerapp show -g "$RG" -n "$WEB_APP_NAME" --query id -o tsv)
JOB_ID=$(az containerapp job show -g "$RG" -n "$FIX_JOB_NAME" --query id -o tsv)

echo "==> Rol atamaları"
assign_role "AcrPush" "$ACR_ID"                      # az acr build + push
assign_role "Contributor" "$WEB_ID"                  # containerapp update
assign_role "Container Apps Jobs Contributor" "$JOB_ID"

cat <<SUMMARY

================= RELEASE OIDC HAZIR =================
GitHub'da şu repository secret'larını tanımlayın:

  gh secret set AZURE_CLIENT_ID       -R $GITHUB_REPO --body "$APP_ID"
  gh secret set AZURE_TENANT_ID       -R $GITHUB_REPO --body "$TENANT_ID"
  gh secret set AZURE_SUBSCRIPTION_ID -R $GITHUB_REPO --body "$SUBSCRIPTION_ID"

Bunlar SIR DEĞİL (kimlik numaraları); parola saklanmıyor. Secret olarak
tutulmalarının sebebi yalnızca loglarda görünmemeleri.

Sonra sürüm yayınlamak için:

  git tag v0.3.2 && git push origin v0.3.2

Rol atamalarının yayılması birkaç dakika sürebilir.
======================================================
SUMMARY
