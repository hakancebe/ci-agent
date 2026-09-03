#!/bin/sh
# Tek image, iki giriş noktası. Hangisinin çalışacağını CI_AGENT_MODE seçer —
# Azure Container Apps'te "bir Container App + bir Container Apps Job, aynı image"
# modeli buna birebir oturuyor: ikisine de aynı image verilir, sadece env var farklıdır.
#
#   CI_AGENT_MODE=web      -> uzun ömürlü webhook servisi   (Container App)   [FAZ 1]
#   CI_AGENT_MODE=fix      -> tek seferlik /fix çalışması    (Container Apps Job)
#   CI_AGENT_MODE=analyze  -> tek seferlik analiz (varsayılan; lokal/fallback)
#
# Not: "fix" ile "analyze" aynı ikiliyi çalıştırır — ayrımı Program.cs'in kendisi
# CI_AGENT_MODE'a bakarak yapıyor. Burada env var'ı ezmiyoruz, aynen geçiriyoruz.
set -e

MODE="${CI_AGENT_MODE:-analyze}"

case "$MODE" in
    web)
        if [ ! -f /app/service/CiAgent.Service.dll ]; then
            echo "HATA: CI_AGENT_MODE=web istendi ama bu image'da web servisi yok." >&2
            echo "      CiAgent.Service FAZ 1'de ekleniyor; bu image yalnızca CLI içeriyor." >&2
            exit 2
        fi
        exec dotnet /app/service/CiAgent.Service.dll "$@"
        ;;
    fix|analyze)
        exec dotnet /app/cli/CiAgent.Cli.dll "$@"
        ;;
    *)
        echo "HATA: bilinmeyen CI_AGENT_MODE: '$MODE' (beklenen: web | fix | analyze)" >&2
        exit 2
        ;;
esac
