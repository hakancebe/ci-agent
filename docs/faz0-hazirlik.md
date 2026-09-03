# Faz 0 — Hazırlık

Amaç: Faz 1'de yazılacak `CiAgent.Service` için **çalışır bir zemin** bırakmak.
Bu fazın sonunda ortada uygulama kodu yok ama şunlar var:

- Kurulabilir bir **GitHub App** (webhook'ları henüz bir yere düşmüyor)
- Azure'da **ACR + ACA environment + managed identity + roller**
- ACR'da **deploy edilebilir bir image** (şimdilik yalnızca CLI içeriyor)
- Secret'ların nerede duracağına dair karar

Repoya konan workflow dosyaları (`.github/workflows/ci-agent*.yml`) **hâlâ
çalışıyor**. Faz 0 hiçbir davranışı değiştirmez; sadece paralel bir yol açar.

---

## 1. GitHub App kaydı

`https://github.com/settings/apps/new` (kişisel hesap) veya
`https://github.com/organizations/<org>/settings/apps/new` (organizasyon).

### Temel alanlar

| Alan | Değer |
|---|---|
| GitHub App name | `CiAgent` (global benzersiz olmalı; doluysa `CiAgent-<hesap>`) |
| Homepage URL | Agent repo URL'i |
| Webhook → Active | ✅ işaretli |
| Webhook URL | Faz 1'e kadar geçici: `https://smee.io/<kanal>` (aşağıya bakın) |
| Webhook secret | `openssl rand -hex 32` çıktısı — **saklayın** |
| Where can this App be installed? | *Only on this account* |

### Repository permissions

Plandaki beş izin, her biri kodun hangi çağrısı için gerekli:

| İzin | Seviye | Neden |
|---|---|---|
| Actions | Read | job listesi + job log indirme (`GetJobsAsync`, `DownloadJobLogAsync`) |
| Checks | Read | check-run annotation'ları (`GetAnnotationsAsync`) |
| Contents | Write | `/fix`'in PR dalına push etmesi + commit yorumu fallback'i |
| Issues | Write | PR yorumu (Issue Comment API) ve 👀 tepkisi |
| Pull requests | Write | commit → PR eşleştirme, PR bilgisini okuma |
| Metadata | Read | zorunlu, otomatik gelir |

> **Neden Contents: Write yeterli, `workflow` izni gerekmiyor?**
> Agent yalnızca kaynak dosyalara dokunuyor. `.github/workflows/` altına yazan
> bir düzeltme üretirse push **reddedilir** — bu bilinçli bir güvenlik sınırı,
> genişletmeyin.

### Subscribe to events

- ✅ **Workflow run** → analiz akışını tetikler
- ✅ **Issue comment** → `/fix` akışını tetikler

### Kayıttan sonra toplanacak üç değer

1. **App ID** — App sayfasının en üstünde
2. **Private key** — "Generate a private key" → inen `.pem` dosyası
   (PKCS#1 formatında, `-----BEGIN RSA PRIVATE KEY-----` ile başlar)
3. **Webhook secret** — yukarıda ürettiğiniz değer

Bu üçü Faz 1'de ACA secret'ı olarak servise verilecek. Şimdilik **repoya
koymayın**; parola yöneticisinde ya da `az keyvault secret set` ile tutun.

### App'i kurun

App sayfası → *Install App* → hedef repo (`ci-agent-pilot`) seçilir.
Kurulumdan sonra adres çubuğundaki `.../installations/<ID>` sayısı
**Installation ID**'dir; Faz 1'de installation token değişimi için lazım
(kodda webhook payload'ından da okunabilir — not almanız şart değil).

### Webhook'u şimdiden görmek isterseniz (opsiyonel)

`https://smee.io/new` bir kanal üretir. Webhook URL'ine onu koyarsanız
GitHub'ın gönderdiği payload'ları tarayıcıdan izleyebilirsiniz — Faz 1'de
`workflow_run` ve `issue_comment` gövdelerinin gerçek şeklini görmek işe yarar.
Faz 1 deploy'undan sonra bu URL gerçek servisle değiştirilecek.

---

## 2. Azure kaynakları

```bash
brew install azure-cli
az login
```

```bash
cp infra/.env.example infra/.env
# infra/.env içindeki ACR_NAME'i benzersiz bir değerle doldurun
./infra/00-provision.sh
```

Script'in kurduğu kaynaklar ve ilerideki rolleri:

| Kaynak | Rolü |
|---|---|
| Resource group | hepsinin kabı |
| ACR (Basic) | image deposu |
| Log Analytics | ACA'nın log hedefi |
| ACA environment | Container App (web) ve Container Apps Job (fix) aynı environment'ta koşacak |
| User-assigned managed identity | ACR'dan image çekme + (Faz 3) Azure OpenAI erişimi |

> **Neden system-assigned değil, user-assigned identity?**
> Aynı kimliği hem Container App hem Container Apps Job kullanacak. System-assigned
> olsaydı ikisi ayrı kimlik olur, rolleri iki kez atamak gerekirdi. Ayrıca ACR'dan
> image çekmek için kimliğin, container app **yaratılmadan önce** var olması ve
> AcrPull rolünü almış olması gerekiyor — user-assigned bunu mümkün kılıyor.

Rol atamaları:

- **AcrPull** (ACR scope'unda) — Faz 1'de image'ı çekebilmek için zorunlu.
- **Cognitive Services OpenAI User** (Azure OpenAI kaynağı scope'unda) —
  Faz 3'te `AZURE_OPENAI_KEY`'i silip managed identity'ye geçerken zorunlu.
  `infra/.env` içindeki `AOAI_RESOURCE_ID` boşsa atlanır.

---

## 3. Image

```bash
./infra/10-build-push.sh v0.0.1
```

`az acr build` kullanır: derleme ACR sunucusunda koşar. İki kazanç —
lokalde Docker Desktop açık olmak zorunda değil, ve **Apple Silicon'da yanlışlıkla
arm64 image üretilmez** (ACA amd64 bekler; arm64 image `exec format error` verir).

Bu image'ın içinde şu an sadece CLI var. Doğrulama:

```bash
docker run --rm <acr>.azurecr.io/ci-agent:v0.0.1 --help          # CLI kullanımını basar
docker run --rm -e CI_AGENT_MODE=web <acr>.azurecr.io/ci-agent:v0.0.1   # "web modu yok" der, exit 2
```

İkinci komutun **hata vermesi beklenen davranış**: `docker/entrypoint.sh` web
modunu tanıyor ama `CiAgent.Service` Faz 1'de eklenecek.

---

## 4. Faz 0 bitti mi? Kontrol listesi

- [ ] GitHub App kayıtlı, 5 izin + 2 event doğru
- [ ] App ID, private key (.pem), webhook secret güvenli bir yerde
- [ ] App hedef repoya kurulu
- [x] `./infra/00-provision.sh` hatasız bitti
- [x] `az acr repository show-tags -n <acr> --repository ci-agent -o table` tag'i gösteriyor — `v0.0.1`
- [x] `docker run ... --help` çalışıyor, `CI_AGENT_MODE=web` anlamlı hata veriyor — 2026-09-02 doğrulandı

---

## Faz 0'da bilerek YAPILMAYANLAR

- Container App / Container Apps Job **oluşturulmadı** — image'da servis yokken
  oluşturmak, sürekli restart eden bir app bırakırdı. Faz 1'de kurulacak.
- Secret'lar ACA'ya **yüklenmedi** — hedefi olmayan secret anlamsız.
- Mevcut workflow dosyalarına **dokunulmadı** — Faz 2 bitene kadar tek çalışan yol onlar.
- `CiAgent.Core` ve `CiAgent.Cli`'de **tek satır değişiklik yok**.
