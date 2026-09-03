# CiAgent — tek image, iki çalışma modu (web / cli).
#
# Neden final katman da SDK, ince `aspnet` runtime değil?
#   /fix modu klonladığı HEDEF repoda `dotnet build` + `dotnet test` çalıştırıp
#   düzeltmenin gerçekten işe yaradığını doğruluyor (CiAgent.Core/BuildRunner.cs).
#   Yani container'ın kendisi bir build makinesi; tam SDK ve `git` şart.
#
# Peki multi-stage neden hâlâ anlamlı?
#   Kaynak ağacı, NuGet ara çıktıları ve test projesi final image'a sızmasın diye.
#   Kazanç boyuttan çok yüzey alanı: çalışan container'da agent'ın kendi kaynağı
#   bulunmaz, sadece publish edilmiş ikilileri bulunur.

# ---------------------------------------------------------------- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Önce YALNIZCA proje dosyaları kopyalanıp restore ediliyor. Docker bir katmanı,
# girdisi (COPY'nin kopyaladığı dosyalar) değişmedikçe yeniden çalıştırmaz; bu
# sırayla bir .cs dosyası değiştiğinde restore cache'ten gelir, sadece build
# tekrarlanır. Tersi sırada (`COPY . .` önce) her küçük değişiklik tam restore
# demek olurdu.
COPY global.json ./
COPY CiAgent.Core/CiAgent.Core.csproj       CiAgent.Core/
COPY CiAgent.Cli/CiAgent.Cli.csproj         CiAgent.Cli/
COPY CiAgent.Service/CiAgent.Service.csproj CiAgent.Service/
RUN dotnet restore CiAgent.Cli/CiAgent.Cli.csproj \
 && dotnet restore CiAgent.Service/CiAgent.Service.csproj

COPY . .

# `--no-restore` BİLEREK verilmiyor. Host'ta kalmış eski bin/obj klasörleri
# (.dockerignore hariç tutmalı ama bazı build backend'leri - ör. `az acr build` -
# context'i tar'larken bunu güvenilir uygulamayabiliyor) `COPY . .` ile üstteki
# restore'un ürettiği project.assets.json'ın üzerine yazılırsa, --no-restore bu
# durumu "paket bulunamadı" (NETSDK1064) diye patlatır çünkü kendini onarmaz.
# Bayrağı kaldırmak publish'e "assets.json şüpheliyse sessizce yeniden restore et"
# der; paketler zaten global NuGet cache'inde olduğu için network'e çıkmaz, sadece
# assets.json'ı tazeler - üstteki restore katmanının cache kazancı bozulmaz.
RUN dotnet publish CiAgent.Cli/CiAgent.Cli.csproj -c Release -o /out/cli
RUN dotnet publish CiAgent.Service/CiAgent.Service.csproj -c Release -o /out/service

# -------------------------------------------------------------- runtime ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runtime

# git, /fix'in klon + commit + push adımları için zorunlu (GitWorkspace.cs `git`
# binary'sini Process ile çağırıyor). SDK image'ında kurulu geliyor; yine de
# kontrol ediyoruz — eksik olsaydı image build'i değil, aylar sonraki ilk /fix
# çalışması patlardı.
RUN if ! command -v git >/dev/null 2>&1; then \
        apt-get update \
     && apt-get install -y --no-install-recommends git \
     && rm -rf /var/lib/apt/lists/*; \
    fi

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    ASPNETCORE_URLS=http://+:8080

# Root olarak koşmuyoruz: bu container hedef repoyu build ediyor, yani üçüncü
# tarafın MSBuild target'larını ve testlerini çalıştırıyor. İzolasyon sınırı
# zaten container ama root olmamak bedava bir kat daha.
#
# İdempotent: .NET 8 SDK image'ının güncel etiketleri artık kutudan çıkma bir
# `app` kullanıcısıyla geliyor (Microsoft'un container sertleştirme çalışması).
# `mcr.microsoft.com/dotnet/sdk:8.0` sabit değil, hareketli bir tag - ACR her
# build'de o anki en güncel image'ı çekiyor, o yüzden "kullanıcı zaten var mı"
# varsayımına güvenmek yerine kontrol ediyoruz.
RUN id -u app >/dev/null 2>&1 \
    || useradd --create-home --uid 1001 app
ENV HOME=/home/app \
    CI_AGENT_WORK_ROOT=/home/app/work

COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

COPY --from=build /out/cli     /app/cli
COPY --from=build /out/service /app/service

# Grup adı sabit "app" varsayılmıyor: kullanıcı base image'dan geldiyse birincil
# grubu farklı adlandırılmış olabilir; `id -gn` o anki gerçek grubu okuyor.
RUN mkdir -p /home/app/work \
 && APP_GROUP="$(id -gn app)" \
 && chown -R "app:$APP_GROUP" /home/app /app
USER app
WORKDIR /home/app

EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
