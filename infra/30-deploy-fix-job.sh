#!/usr/bin/env bash
# FAZ 2 — /fix'i çalıştıran Container Apps Job'ı kurar/günceller ve web servisine
# onu tetikleme yetkisi verir. Idempotent.
#
# Neden ayrı bir job, webhook servisinin içinde değil?
#   /fix, üçüncü tarafın PR'ındaki kodu klonlayıp `dotnet build` + `dotnet test`
#   çalıştırıyor. Bunu webhook'lara cevap veren sürecin içinde yapmak, düşmanca
#   bir PR'ın servisi etkileyebilmesi ve dakikalarca süren bir build'in webhook
#   işleyicisini aç bırakması demekti. Ayrı job = her çalıştırma için taze,
#   kendi kaynak sınırı olan, işi bitince ölen bir container.
set -euo pipefail

cd "$(dirname "$0")"
[ -f .env ] || { echo "HATA: infra/.env yok." >&2; exit 1; }
# shellcheck disable=SC1091
set -a; . ./.env; set +a
# shellcheck disable=SC1091
[ -f .env.secrets ] && { set -a; . ./.env.secrets; set +a; }

# shellcheck disable=SC1091
. ./_common.sh

backfill_from_user_secrets \
    GITHUB_APP_ID GITHUB_APP_PRIVATE_KEY_PATH \
    AZURE_OPENAI_ENDPOINT AZURE_OPENAI_KEY AZURE_OPENAI_DEPLOYMENT

TAG="${1:-}"
[ -n "$TAG" ] || { echo "Kullanım: $0 <tag>   ör: $0 v0.2.0" >&2; exit 1; }

JOB_NAME="${FIX_JOB_NAME:-ci-agent-fix}"
WEB_APP_NAME="${WEB_APP_NAME:-ci-agent-web}"

missing=()
REQUIRED=(GITHUB_APP_ID GITHUB_APP_PRIVATE_KEY_PATH AZURE_OPENAI_ENDPOINT AZURE_OPENAI_DEPLOYMENT)
# Anahtar yalnızca managed identity kapalıysa zorunlu.
use_managed_identity || REQUIRED+=(AZURE_OPENAI_KEY)

for v in "${REQUIRED[@]}"; do
    [ -z "${!v:-}" ] && missing+=("$v")
done

if [ ${#missing[@]} -gt 0 ]; then
    echo "HATA: Şu değerler eksik: ${missing[*]}" >&2
    echo "20-deploy-web.sh ile aynı kaynaklardan okunuyor (.env.secrets ya da dotnet user-secrets)." >&2
    exit 1
fi

[ -f "$GITHUB_APP_PRIVATE_KEY_PATH" ] \
    || { echo "HATA: private key bulunamadı: '$GITHUB_APP_PRIVATE_KEY_PATH'" >&2; exit 1; }

validate_openai_key || exit 1
load_appinsights_connection_string

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
ACR_SERVER=$(az acr show -n "$ACR_NAME" -g "$RG" --query loginServer -o tsv)
IDENTITY_ID=$(az identity show -g "$RG" -n "$IDENTITY_NAME" --query id -o tsv)
IDENTITY_PRINCIPAL=$(az identity show -g "$RG" -n "$IDENTITY_NAME" --query principalId -o tsv)
IDENTITY_CLIENT=$(az identity show -g "$RG" -n "$IDENTITY_NAME" --query clientId -o tsv)
IMAGE="$ACR_SERVER/ci-agent:$TAG"

PRIVATE_KEY_ESCAPED=$(awk '{printf "%s\\n", $0}' "$GITHUB_APP_PRIVATE_KEY_PATH")

# Job'a GITHUB_TOKEN VERİLMİYOR. Bunun yerine App private key'i ve installation
# id'si veriliyor; token container'ın içinde, kullanılacağı anda üretiliyor.
# Sebep: hazır token, ARM API çağrısının gövdesinde ve execution'ın env var
# listesinde görünür olurdu - yani Azure Activity Log'a düşerdi.
# Managed identity modunda azure-openai-key ne secret olarak yazılıyor ne de
# env var olarak bağlanıyor — servis anahtarın yokluğundan managed identity'ye
# geçmesi gerektiğini anlıyor (LlmServiceFactory).
SECRETS=(
    "github-app-private-key=$PRIVATE_KEY_ESCAPED"
)

ENV_VARS=(
    "CI_AGENT_MODE=fix"
    "CI_AGENT_CLONE=true"
    "GITHUB_APP_ID=$GITHUB_APP_ID"
    "GITHUB_APP_PRIVATE_KEY=secretref:github-app-private-key"
    "AZURE_OPENAI_ENDPOINT=$AZURE_OPENAI_ENDPOINT"
    "AZURE_OPENAI_DEPLOYMENT=$AZURE_OPENAI_DEPLOYMENT"
)

if ! use_managed_identity; then
    SECRETS+=("azure-openai-key=$AZURE_OPENAI_KEY")
    ENV_VARS+=("AZURE_OPENAI_KEY=secretref:azure-openai-key")
fi

# Bu, yalnızca job'ın BAŞLANGIÇ tanımına yazıyor. Gerçek /fix çalışmalarında
# ContainerAppJobRunner.BuildEnvironment her seferinde tanımı PATCH'lediği
# için AYNI değeri orada da yeniden vermek ŞART — yoksa ilk /fix'te sessizce
# silinir (AZURE_OPENAI_KEY'de yaşanan hatanın aynısı, bkz. o dosyanın yorumu).
if [ -n "${APPLICATIONINSIGHTS_CONNECTION_STRING:-}" ]; then
    ENV_VARS+=("APPLICATIONINSIGHTS_CONNECTION_STRING=$APPLICATIONINSIGHTS_CONNECTION_STRING")
fi

if az containerapp job show -g "$RG" -n "$JOB_NAME" >/dev/null 2>&1; then
    echo "==> Mevcut job güncelleniyor: $JOB_NAME"
    az containerapp job secret set -g "$RG" -n "$JOB_NAME" \
        --secrets "${SECRETS[@]}" --only-show-errors >/dev/null
    # --replace-env-vars env'i komple değiştiriyor, yani AZURE_OPENAI_KEY listede
    # yoksa kendiliğinden kalkıyor. Secret'ı ayrıca kaldırıyoruz.
    az containerapp job update -g "$RG" -n "$JOB_NAME" \
        --image "$IMAGE" \
        --replace-env-vars "${ENV_VARS[@]}" --only-show-errors >/dev/null

    if use_managed_identity; then
        az containerapp job secret remove -g "$RG" -n "$JOB_NAME" \
            --secret-names azure-openai-key --yes --only-show-errors >/dev/null 2>&1 || true
    fi
else
    echo "==> Job oluşturuluyor: $JOB_NAME"
    # trigger-type Manual: job kendiliğinden çalışmıyor, yalnızca web servisi
    # ARM API üzerinden başlattığında koşuyor.
    #
    # replica-timeout 1800 sn (30 dk): klon + restore + build + test + LLM turları
    # uzun sürebiliyor. Servis tarafındaki bekleme 20 dk; job'a biraz daha geniş
    # pay bırakmak, servisin beklemeyi bıraktığı bir işin yine de tamamlanıp
    # sonucu PR'a yazabilmesi demek.
    az containerapp job create -g "$RG" -n "$JOB_NAME" \
        --environment "$ACA_ENV" \
        --trigger-type Manual \
        --replica-timeout 1800 \
        --replica-retry-limit 0 \
        --image "$IMAGE" \
        --cpu 2 --memory 4Gi \
        --registry-server "$ACR_SERVER" \
        --registry-identity "$IDENTITY_ID" \
        --mi-user-assigned "$IDENTITY_ID" \
        --secrets "${SECRETS[@]}" \
        --env-vars "${ENV_VARS[@]}" \
        --only-show-errors >/dev/null
fi

JOB_ID=$(az containerapp job show -g "$RG" -n "$JOB_NAME" --query id -o tsv)

# --- Web servisine job'ı tetikleme yetkisi -----------------------------------
# "Container Apps Jobs Contributor" olmadan servis ARM'a start çağrısı yapamaz
# ve /fix sessizce 403 alır.
echo "==> Rol ataması: Container Apps Jobs Contributor"
if az role assignment list --assignee "$IDENTITY_PRINCIPAL" --scope "$JOB_ID" \
     --query "[?roleDefinitionName=='Container Apps Jobs Contributor']" -o tsv | grep -q .; then
    echo "    zaten var"
else
    az role assignment create \
        --assignee-object-id "$IDENTITY_PRINCIPAL" \
        --assignee-principal-type ServicePrincipal \
        --role "Container Apps Jobs Contributor" \
        --scope "$JOB_ID" --only-show-errors >/dev/null
    echo "    atandı"
fi

# --- Web servisine /fix ayarlarını bildir ------------------------------------
echo "==> Web servisi /fix için yapılandırılıyor"
az containerapp update -g "$RG" -n "$WEB_APP_NAME" \
    --set-env-vars \
        "AZURE_SUBSCRIPTION_ID=$SUBSCRIPTION_ID" \
        "AZURE_RESOURCE_GROUP=$RG" \
        "AZURE_CLIENT_ID=$IDENTITY_CLIENT" \
        "CI_AGENT_FIX_JOB_NAME=$JOB_NAME" \
        "CI_AGENT_FIX_JOB_IMAGE=$IMAGE" \
    --only-show-errors >/dev/null

cat <<SUMMARY

================= FAZ 2 /fix HAZIR =================
  Job           : $JOB_NAME
  Image         : $IMAGE
  Web servisi   : $WEB_APP_NAME (artık issue_comment olaylarını işliyor)

Rol ataması birkaç dakika yayılabilir; hemen denenen ilk /fix 403 alabilir.

Test: App'in kurulu olduğu bir repoda, CI'ı patlamış bir PR'a "/fix" yorumu yazın.

  az containerapp logs show -g $RG -n $WEB_APP_NAME --follow
  az containerapp job execution list -g $RG -n $JOB_NAME -o table
====================================================
SUMMARY
