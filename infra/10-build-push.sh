#!/usr/bin/env bash
# Image'ı derleyip ACR'a push eder.
#
# Varsayılan olarak `az acr build` kullanılır: build ACR'ın kendi sunucusunda
# koşar, yani lokalde Docker Desktop'ın açık olması GEREKMEZ ve amd64 image
# üretilir. Apple Silicon'da lokal `docker build` arm64 üretip ACA'da
# "exec format error" verirdi — bu tuzağı baştan atlıyoruz.
#
# Lokalde denemek isterseniz: ./10-build-push.sh v0.0.1 --local
# (Docker Desktop açık olmalı; --platform linux/amd64 zorlanır.)
set -euo pipefail

cd "$(dirname "$0")"
[ -f .env ] || { echo "HATA: infra/.env yok." >&2; exit 1; }
# shellcheck disable=SC1091
set -a; . ./.env; set +a

TAG="${1:-}"
[ -n "$TAG" ] || { echo "Kullanım: $0 <tag> [--local]   ör: $0 v0.0.1" >&2; exit 1; }
MODE="${2:-remote}"

CONTEXT="$(cd .. && pwd)"     # ci-agent/ dizini = Docker build context
IMAGE="ci-agent:$TAG"

# Host'ta lokal `dotnet build/test/publish` çalıştırılmışsa kalan bin/obj,
# .dockerignore'a rağmen build backend'inin context'e dahil etmesi ihtimaline
# karşı burada da temizleniyor - NETSDK1064 ("Package X was not found") hatasının
# en yaygın sebebi bu: eski obj/project.assets.json, restore'un tazesinin üzerine
# yazılıp publish'i şaşırtıyor.
echo "==> Host'taki bin/obj temizleniyor"
find "$CONTEXT" -type d \( -name bin -o -name obj \) -not -path "*/infra/*" -prune -exec rm -rf {} +

if [ "$MODE" = "--local" ]; then
    command -v docker >/dev/null || { echo "HATA: docker yok." >&2; exit 1; }
    docker info >/dev/null 2>&1 || { echo "HATA: Docker daemon çalışmıyor (Docker Desktop'ı açın)." >&2; exit 1; }
    ACR_SERVER=$(az acr show -n "$ACR_NAME" -g "$RG" --query loginServer -o tsv)
    az acr login -n "$ACR_NAME"
    docker build --platform linux/amd64 -t "$ACR_SERVER/$IMAGE" "$CONTEXT"
    docker push "$ACR_SERVER/$IMAGE"
else
    echo "==> az acr build (sunucu tarafında, linux/amd64)"
    az acr build --registry "$ACR_NAME" --image "$IMAGE" --platform linux/amd64 "$CONTEXT"
    ACR_SERVER=$(az acr show -n "$ACR_NAME" -g "$RG" --query loginServer -o tsv)
fi

echo
echo "Push edildi: $ACR_SERVER/$IMAGE"
echo "Doğrulama:  az acr repository show-tags -n $ACR_NAME --repository ci-agent -o table"
