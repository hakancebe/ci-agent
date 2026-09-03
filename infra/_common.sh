#!/usr/bin/env bash
# Deploy script'lerinin paylaştığı yardımcılar. Doğrudan çalıştırılmaz, source edilir.
#
# Buradaki asıl değer `validate_openai_key`: bir kez, yanlış bir anahtarla yapılan
# deploy hem web servisini hem /fix job'ını sessizce bozdu. Bozukluk deploy anında
# değil, saatler sonra ilk gerçek analizde "HTTP 401" olarak ortaya çıktı — yani
# hatayla sebebi arasında saatler ve birkaç yanlış teşhis vardı.

# --- Yapılandırma kaynakları -------------------------------------------------
# Öncelik: shell'e export edilmiş > infra/.env.secrets > dotnet user-secrets.
#
# Bu sıralamanın bir tuzağı var ve bilerek kayıt altına alınıyor: kabuğunuzda
# ESKİ bir değer duruyorsa, .env.secrets ve user-secrets'taki doğru değeri
# sessizce ezer. Aşağıdaki doğrulama tam da bunun için var.

# Değerin nereden geldiğini takip ediyoruz ki hata mesajı "hangi anahtar yanlış"
# değil, "hangi KAYNAKTAKİ anahtar yanlış" diyebilsin.
declare -A CI_AGENT_VALUE_SOURCE

load_user_secrets_path () {
    local cli_csproj="../CiAgent.Cli/CiAgent.Cli.csproj"
    local id
    id=$(grep -o '<UserSecretsId>[^<]*' "$cli_csproj" 2>/dev/null | cut -d'>' -f2 || true)
    [ -n "$id" ] && echo "$HOME/.microsoft/usersecrets/$id/secrets.json"
}

from_user_secrets () {  # $1=anahtar adı
    local path
    path=$(load_user_secrets_path)
    [ -n "$path" ] && [ -f "$path" ] || return 1

    # dotnet'in yazdığı JSON UTF-8 BOM'lu olabiliyor; utf-8-sig BOM'u yutuyor.
    python3 - "$path" "$1" <<'PY' 2>/dev/null
import json, sys
with open(sys.argv[1], encoding="utf-8-sig") as f:
    data = json.load(f)
value = data.get(sys.argv[2])
if not value:
    sys.exit(1)
print(value)
PY
}

# Verilen değişkenlerden boş olanları user-secrets'tan doldurur ve her birinin
# hangi kaynaktan geldiğini kaydeder.
backfill_from_user_secrets () {
    local v value
    for v in "$@"; do
        if [ -n "${!v:-}" ]; then
            CI_AGENT_VALUE_SOURCE["$v"]="${CI_AGENT_VALUE_SOURCE[$v]:-shell/.env.secrets}"
            continue
        fi

        value="$(from_user_secrets "$v" || true)"
        if [ -n "$value" ]; then
            export "$v=$value"
            CI_AGENT_VALUE_SOURCE["$v"]="dotnet user-secrets"
        fi
    done
}

# App Insights bağlantı dizesini kaynaktan okur. Bulunamazsa sessizce boş
# bırakılıyor: izleme opsiyonel, yokluğu deploy'u durdurmamalı.
load_appinsights_connection_string () {
    [ -n "${APPLICATIONINSIGHTS_CONNECTION_STRING:-}" ] && return 0
    [ -n "${APPINSIGHTS_NAME:-}" ] || return 0

    local conn
    conn=$(az monitor app-insights component show -g "$RG" -a "$APPINSIGHTS_NAME" \
        --query connectionString -o tsv 2>/dev/null || true)

    [ -n "$conn" ] && export APPLICATIONINSIGHTS_CONNECTION_STRING="$conn"
    return 0
}

# --- Doğrulama ---------------------------------------------------------------

# CI_AGENT_USE_MANAGED_IDENTITY=true ise Azure OpenAI anahtarı ACA'ya HİÇ
# yazılmıyor; servis token'ı managed identity ile alıyor. Prod için önerilen mod:
# saklanacak, kopyalanacak ve eskiyebilecek bir değer kalmıyor.
use_managed_identity () {
    [ "${CI_AGENT_USE_MANAGED_IDENTITY:-true}" = "true" ]
}

# Azure OpenAI anahtarını GERÇEKTEN deneyerek doğrular. Deploy'dan ÖNCE
# çağrılmalı: geçersiz bir anahtarı ACA'ya yazmak, çalışan bir servisi bozmak
# demek ve geri alması manuel iş gerektiriyor.
validate_openai_key () {
    if use_managed_identity; then
        echo "==> Azure OpenAI: managed identity kullanılacak, anahtar doğrulaması atlandı"
        echo "    (anahtar ACA'ya hiç yazılmıyor)"
        return 0
    fi

    local endpoint="${AZURE_OPENAI_ENDPOINT%/}/"
    local source="${CI_AGENT_VALUE_SOURCE[AZURE_OPENAI_KEY]:-bilinmiyor}"
    local code

    echo "==> Azure OpenAI anahtarı doğrulanıyor (kaynak: $source)"

    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 \
        -X POST "${endpoint}chat/completions" \
        -H "Authorization: Bearer ${AZURE_OPENAI_KEY}" \
        -H 'Content-Type: application/json' \
        -d "{\"model\":\"${AZURE_OPENAI_DEPLOYMENT}\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":1}" \
        2>/dev/null)

    if [ "$code" = "200" ]; then
        echo "    geçerli (HTTP 200)"
        return 0
    fi

    cat >&2 <<EOF

HATA: Azure OpenAI anahtarı çalışmıyor (HTTP $code) — deploy DURDURULDU.

  Kullanılan kaynak : $source
  Endpoint          : $endpoint
  Deployment        : $AZURE_OPENAI_DEPLOYMENT

Deploy'a devam etseydik bu anahtar ACA'ya yazılacak ve çalışan servisi
bozacaktı; hata da ancak ilk gerçek analizde ortaya çıkacaktı.

En sık sebep: kabuğunuzda ESKİ bir AZURE_OPENAI_KEY export edilmiş olması.
Kabuktaki değer, .env.secrets ve user-secrets'taki doğru değeri ezer. Kontrol:

    echo \${AZURE_OPENAI_KEY:0:6}...     # kabuktaki değer
    unset AZURE_OPENAI_KEY               # kaldırıp tekrar deneyin

EOF
    return 1
}
