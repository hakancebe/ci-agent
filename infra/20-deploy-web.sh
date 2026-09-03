#!/usr/bin/env bash
# FAZ 1 — webhook servisini Container App olarak kurar/günceller. Idempotent.
#
# Secret'lar ACA secret'ı olarak saklanıp env var'a secretref ile bağlanıyor:
# `az containerapp show` çıktısında değerleri GÖRÜNMEZ, yalnızca referansları görünür.
set -euo pipefail

cd "$(dirname "$0")"
[ -f .env ] || { echo "HATA: infra/.env yok. 'cp .env.example .env' ile başlayın." >&2; exit 1; }
# shellcheck disable=SC1091
set -a; . ./.env; set +a

# Secret'lar .env'den AYRI bir dosyada: .env altyapı adlarını tutuyor ve paylaşılabilir,
# .env.secrets ise App private key'i gibi değerleri tutuyor. İkisi de gitignore'da,
# ama ayırmak "hangi dosyayı kimseyle paylaşamam" sorusunu netleştiriyor.
# shellcheck disable=SC1091
[ -f .env.secrets ] && { set -a; . ./.env.secrets; set +a; }

# Ortak yardımcılar: user-secrets geri dönüşü ve anahtar doğrulaması.
# shellcheck disable=SC1091
. ./_common.sh

backfill_from_user_secrets \
    GITHUB_APP_ID GITHUB_APP_PRIVATE_KEY_PATH GITHUB_WEBHOOK_SECRET \
    AZURE_OPENAI_ENDPOINT AZURE_OPENAI_KEY AZURE_OPENAI_DEPLOYMENT

TAG="${1:-}"
[ -n "$TAG" ] || { echo "Kullanım: $0 <tag>   ör: $0 v0.1.0" >&2; exit 1; }

APP_NAME="${WEB_APP_NAME:-ci-agent-web}"

# --- Secret'lar ---------------------------------------------------------------
# Eksik değerlerin HEPSİ toplanıp tek seferde bildiriliyor. Tek tek bildirmek
# (`${VAR:?}` idiomunun yaptığı) altı değer için altı kez çalıştırmak demekti.
# ServiceOptions.FromConfiguration da C# tarafında aynı şeyi yapıyor.
#
# Öncelik sırası: shell'e export edilmiş > infra/.env.secrets > dotnet user-secrets.
missing=()
require () {  # $1=değişken adı  $2=nereden bulunur
    if [ -z "${!1:-}" ]; then
        missing+=("  $1
      → $2")
    fi
}

require GITHUB_APP_ID \
    "GitHub App sayfasının üstündeki 'App ID' (ör: 123456)"
require GITHUB_APP_PRIVATE_KEY_PATH \
    "App sayfasından indirdiğiniz .pem dosyasının yolu (ör: ~/Downloads/ciagent.private-key.pem)"
require GITHUB_WEBHOOK_SECRET \
    "App kaydında 'Webhook secret' alanına girdiğiniz değer"
require AZURE_OPENAI_ENDPOINT \
    "az cognitiveservices account show -n <ad> -g <rg> --query properties.endpoint -o tsv"
# Anahtar yalnızca managed identity KAPALIYSA zorunlu.
use_managed_identity || require AZURE_OPENAI_KEY \
    "az cognitiveservices account keys list -n <ad> -g <rg> --query key1 -o tsv"
require AZURE_OPENAI_DEPLOYMENT \
    "Azure OpenAI'daki model deployment adı (ör: gpt-4o)"

if [ ${#missing[@]} -gt 0 ]; then
    cat >&2 <<EOF

HATA: Şu değerler eksik:

$(printf '%s\n' "${missing[@]}")

İki yoldan biriyle verilebilir (export ETMEDEN):

  1) CiAgent.Cli'ın zaten kullandığı dotnet user-secrets store'una ekleyin
     (AZURE_OPENAI_* muhtemelen orada zaten var, yalnızca eksikleri girin):

       cd ../CiAgent.Cli
       dotnet user-secrets set GITHUB_APP_ID "123456"
       dotnet user-secrets set GITHUB_APP_PRIVATE_KEY_PATH "/tam/yol/private-key.pem"
       dotnet user-secrets set GITHUB_WEBHOOK_SECRET "webhook-secret-degeriniz"

  2) infra/.env.secrets dosyasına yazın (şablon: .env.secrets.example):

       cp .env.secrets.example .env.secrets
       \$EDITOR .env.secrets

İkisi de repodan ayrı yaşıyor; hiçbiri git'e girmez.

EOF
    exit 1
fi

[ -f "$GITHUB_APP_PRIVATE_KEY_PATH" ] \
    || { echo "HATA: private key dosyası bulunamadı: '$GITHUB_APP_PRIVATE_KEY_PATH'" >&2; exit 1; }

# Anahtar ACA'ya YAZILMADAN önce doğrulanıyor: geçersiz bir anahtarı yazmak
# çalışan servisi bozar ve hata ancak ilk analizde ortaya çıkar.
validate_openai_key || exit 1
load_appinsights_connection_string

ACR_SERVER=$(az acr show -n "$ACR_NAME" -g "$RG" --query loginServer -o tsv)
IDENTITY_ID=$(az identity show -g "$RG" -n "$IDENTITY_NAME" --query id -o tsv)
IMAGE="$ACR_SERVER/ci-agent:$TAG"

# PEM çok satırlı; ACA secret değeri tek satır olmak zorunda değil ama shell
# üzerinden geçerken satır sonları kaybolabiliyor. Servis "\n" kaçışlarını
# çözdüğü için (ServiceOptions.ReadPrivateKey) burada kaçırarak gönderiyoruz.
PRIVATE_KEY_ESCAPED=$(awk '{printf "%s\\n", $0}' "$GITHUB_APP_PRIVATE_KEY_PATH")

# Managed identity modunda azure-openai-key ne secret olarak yazılıyor ne de
# env var olarak bağlanıyor — servis anahtarın yokluğundan managed identity'ye
# geçmesi gerektiğini anlıyor (LlmServiceFactory).
SECRETS=(
    "github-app-private-key=$PRIVATE_KEY_ESCAPED"
    "github-webhook-secret=$GITHUB_WEBHOOK_SECRET"
)

ENV_VARS=(
    "CI_AGENT_MODE=web"
    "GITHUB_APP_ID=$GITHUB_APP_ID"
    "GITHUB_APP_PRIVATE_KEY=secretref:github-app-private-key"
    "GITHUB_WEBHOOK_SECRET=secretref:github-webhook-secret"
    "AZURE_OPENAI_ENDPOINT=$AZURE_OPENAI_ENDPOINT"
    "AZURE_OPENAI_DEPLOYMENT=$AZURE_OPENAI_DEPLOYMENT"
    "CI_AGENT_WATCHED_WORKFLOWS=${CI_AGENT_WATCHED_WORKFLOWS:-CI}"
)

# İzleme opsiyonel: bağlantı dizesi yoksa servis izlemesiz ama sorunsuz çalışır.
if [ -n "${APPLICATIONINSIGHTS_CONNECTION_STRING:-}" ]; then
    ENV_VARS+=("APPLICATIONINSIGHTS_CONNECTION_STRING=$APPLICATIONINSIGHTS_CONNECTION_STRING")
fi

if ! use_managed_identity; then
    SECRETS+=("azure-openai-key=$AZURE_OPENAI_KEY")
    ENV_VARS+=("AZURE_OPENAI_KEY=secretref:azure-openai-key")
fi

if az containerapp show -g "$RG" -n "$APP_NAME" >/dev/null 2>&1; then
    echo "==> Mevcut Container App güncelleniyor: $APP_NAME"
    az containerapp secret set -g "$RG" -n "$APP_NAME" \
        --secrets "${SECRETS[@]}" --only-show-errors >/dev/null
    # --set-env-vars BİRLEŞTİRİYOR, değiştirmiyor: managed identity'ye geçerken
    # eski AZURE_OPENAI_KEY açıkça kaldırılmazsa env'de kalır ve servis anahtarı
    # görüp managed identity'ye HİÇ geçmez — göçün sessizce başarısız olması.
    REMOVE_ARGS=()
    use_managed_identity && REMOVE_ARGS=(--remove-env-vars AZURE_OPENAI_KEY)

    az containerapp update -g "$RG" -n "$APP_NAME" \
        --image "$IMAGE" \
        --set-env-vars "${ENV_VARS[@]}" "${REMOVE_ARGS[@]}" --only-show-errors >/dev/null

    # Secret da kaldırılıyor: env var gitse bile secret'ın kalması "prod'da sır yok"
    # iddiasını boşa çıkarırdı.
    if use_managed_identity; then
        az containerapp secret remove -g "$RG" -n "$APP_NAME" \
            --secret-names azure-openai-key --yes --only-show-errors >/dev/null 2>&1 || true
    fi
else
    echo "==> Container App oluşturuluyor: $APP_NAME"
    # min-replicas=1 ŞART: 0'a inerse bellekteki kuyruk ve dedup kaydı silinir,
    # üstelik soğuk başlangıç GitHub'ın ~10 sn webhook timeout'una takılabilir.
    az containerapp create -g "$RG" -n "$APP_NAME" \
        --environment "$ACA_ENV" \
        --image "$IMAGE" \
        --registry-server "$ACR_SERVER" \
        --registry-identity "$IDENTITY_ID" \
        --user-assigned "$IDENTITY_ID" \
        --ingress external --target-port 8080 \
        --min-replicas 1 --max-replicas 1 \
        --secrets "${SECRETS[@]}" \
        --env-vars "${ENV_VARS[@]}" \
        --only-show-errors >/dev/null
fi

FQDN=$(az containerapp show -g "$RG" -n "$APP_NAME" --query properties.configuration.ingress.fqdn -o tsv)

cat <<SUMMARY

================= FAZ 1 SERVİS HAZIR =================
  Container App : $APP_NAME
  Image         : $IMAGE
  Webhook URL   : https://$FQDN/webhooks/github
  Health        : https://$FQDN/health

Sıradaki adım — GitHub App ayarlarında Webhook URL'ini yukarıdaki adresle
değiştirin (smee.io yerine), ardından:

  curl -s https://$FQDN/health
  az containerapp logs show -g $RG -n $APP_NAME --follow
======================================================
SUMMARY
