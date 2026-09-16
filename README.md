# CiAgent

GitHub Actions'da bir CI/CD çalışması başarısız olduğunda logu otomatik analiz edip
PR'a (ya da commit'e) kök nedenini ve önerilen çözümü yorum olarak yazan; istenirse
düzeltmeyi kendisi deneyip doğrulayan bir agent.

## Ne yapar

**Analiz** — İzlenen bir workflow `failure` ile bittiğinde:
1. Başarısız job'ların loglarını ve annotation'larını indirir.
2. Hatayı ayrıştırır, mümkünse `dosya:satır` konumunu ve ilgili kod kesitini bulur.
3. Azure OpenAI ile kök nedeni ve önerilen çözümü çıkarır.
4. Sonucu PR'a (PR yoksa commit'e) Markdown yorum olarak yazar.

**`/fix`** — Yetkili biri PR'a `/fix` yazınca:
1. Analiz sonucunu (mümkünse önceki yorumdan tekrar hesaplamadan) kullanır.
2. Değişikliği "bul-değiştir" olarak üretir, güvenlik politikasından geçirir.
3. Uygular, derler ve test eder; geçmezse geri alıp farklı bir yaklaşımla tekrar dener.
4. `/fix` yalnızca sonucu yorum olarak gösterir; `/fix --commit` başarılı düzeltmeyi PR dalına push eder.

Analiz **dilden bağımsız** çalışır (ham log tabanlı). `/fix` şu an üç ekosistemi
destekliyor: **.NET**, **Python**, **Node.js**.

## Nasıl çalışıyor

```
GitHub ──webhook (workflow_run / issue_comment)──▶ CiAgent.Service
                                                        │
                                          imza doğrula → kuyruğa al → 202
                                                        │
                                              arka planda AnalysisWorker
                                                        │
                                              CiAnalysisPipeline (Core)
                                                        │
                                    log/annotation oku → LLM'e sor → PR'a yaz
                                                        │
                                          (/fix ise) ayrı bir Container Apps Job
                                          repoyu klonlar, düzeltir, doğrular, push eder
```

Kimlik doğrulama iki yerde de "saklanan sır yok" ilkesiyle çalışır: GitHub'a GitHub
App'in kısa ömürlü installation token'ıyla, Azure OpenAI'a ise (mümkünse) managed
identity ile bağlanılır.

## Proje yapısı

| Proje | Görevi |
|---|---|
| `CiAgent.Core` | Analiz ve `/fix` mantığının tamamı (`Analysis/`, `Llm/`, `GitHub/`, `Fix/`, `Common/`) |
| `CiAgent.Cli` | Komut satırı adaptörü — CI job'unda ya da lokalde çalıştırma |
| `CiAgent.Service` | Webhook alıcı (`Webhook/`) + arka plan işçisi (`Worker/`), Azure Container Apps'te çalışır |
| `CiAgent.Tests` | xUnit + Moq, ~500 test |

`Core`'daki mantık hem `Cli` hem `Service` tarafından **aynı şekilde** çağrılır —
tetikleyici (komut satırı / webhook) değişse de analiz ve `/fix` davranışı tek yerden yönetilir.

## Yerel çalıştırma

```bash
dotnet build CiAgent.sln
dotnet test CiAgent.sln
```

CLI ile deneme (analiz):

```bash
dotnet run --project CiAgent.Cli -- <owner> <repo> <runId> --dry-run
```

`--dry-run` analiz yapar (Azure OpenAI çağrısı dahil) ama GitHub'a hiçbir şey yazmaz.

## Yapılandırma

| Değişken | Kullanan | Açıklama |
|---|---|---|
| `GITHUB_TOKEN` | Cli | GitHub API erişimi (yoksa App kimliğinden üretilir) |
| `GITHUB_APP_ID`, `GITHUB_APP_PRIVATE_KEY` | Cli, Service | GitHub App kimliği |
| `GITHUB_WEBHOOK_SECRET` | Service | Gelen webhook'ların HMAC doğrulaması |
| `CI_AGENT_INSTALLATION_ID` | Cli | Kendi kendine token üretirken hedef installation |
| `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT` | Cli, Service | Azure OpenAI hedefi |
| `AZURE_OPENAI_KEY` | Cli, Service | Opsiyonel — verilmezse managed identity kullanılır |
| `AZURE_CLIENT_ID` | Cli, Service | User-assigned managed identity seçimi |
| `CI_AGENT_MODE` | Cli | `analyze` (varsayılan) / `fix` |
| `CI_AGENT_WATCHED_WORKFLOWS` | Service | İzlenecek workflow adları (varsayılan `CI,CD`) |
| `CI_AGENT_MAX_JOBS_PER_HOUR` | Service | Installation başına hız sınırı (varsayılan 20) |
| `CI_AGENT_ANALYZE_CANCELLED` | Service | `cancelled` biten run'ları da analiz et |
| `CI_AGENT_DRY_RUN` | Cli | `--dry-run` ile aynı etki |

## Dağıtım

Tek Docker image, `CI_AGENT_MODE` ortam değişkenine göre iki farklı rolde çalışır:
sürekli açık webhook servisi (Azure Container App) ya da tek seferlik `/fix` işi
(Azure Container Apps Job). Deploy, bir `v*` sürüm etiketi atıldığında
`.github/workflows/release.yml` üzerinden OIDC ile (saklanan sır olmadan) Azure'a
yapılır — `main`'e yapılan sıradan bir push deploy tetiklemez.

Daha fazla detay için: [`docs/`](docs).

## Güvenlik ilkeleri

- `/fix`, `.github/workflows/` altına, test dosyalarına ve repo dışına asla yazmaz.
- Model'in ürettiği değişiklik, dosyada **birebir ve tek bir yerde** eşleşmiyorsa reddedilir.
- Derleme/test doğrulaması geçmeyen bir değişiklik hiçbir zaman commit'lenmez.
- Log ve prompt içerikleri modele gitmeden önce token, parola ve e-posta gibi sırlar maskelenir.
- `/fix` komutunu yalnızca repo sahibi, organizasyon üyesi ya da davetli katkıcılar çalıştırabilir.
- Fork'tan gelen PR'larda `/fix` en baştan reddedilir (agent'ın token'ı fork'a push edemez).

## Sınırlar

- Kuyruk yalnızca bellekte; ani bir çökmede sıradaki iş sessizce kaybolabilir (elle "Redeliver" ile telafi edilir).
- CI yeşil ama uygulama canlıda bozuksa (test kapsama boşluğu), agent'ın bakacağı bir hata sinyali yoktur.
- `/fix`, .NET/Python/Node.js dışındaki dillerde devreye girmez; analiz yine de çalışır.
