# Faz 3 — Sertleştirme ve otomasyon

Faz 1 analizi, Faz 2 `/fix`'i buluta taşımıştı. Faz 3 bunları **prod'da güvenle
bırakılabilir** hale getiriyor: sır sayısını azaltmak, maliyeti sınırlamak,
görünürlük kazanmak ve deploy'u insan elinden çıkarmak.

---

## 1. Managed identity — `AZURE_OPENAI_KEY` prod'dan kalktı

**Önce:** Azure OpenAI anahtarı bir ACA secret'ı olarak hem web servisinde hem
`/fix` job'ında duruyordu.

**Sonra:** Hiçbirinde yok. Token, her istekte platformdan (user-assigned managed
identity ile) alınıyor.

### Bunu neden yaptık — gerçek bir olaydan

Faz 2 sırasında şu yaşandı: geliştiricinin kabuğunda **eski** bir
`AZURE_OPENAI_KEY` duruyordu. Deploy script'leri değerleri
*shell env → .env.secrets → user-secrets* sırasıyla aradığı için o eski anahtar
doğru sanılıp ACA'ya yazıldı. Sonuç:

- Deploy "başarılı" dedi.
- Hem web servisi hem `/fix` job'ı bozuldu.
- Hata ancak **saatler sonra**, `/fix` çalışırken `HTTP 401` olarak ortaya çıktı.
- Teşhis, ARM'ın `secretRef` davranışını suçlayan iki yanlış hipotezden geçti.

Kök sebep "yanlış anahtar" değil, **saklanan bir değerin iki yerde farklı
olabilmesi**ydi. Managed identity bu sınıfı ortadan kaldırıyor: saklanan,
kopyalanan ve eskiyen bir değer yoksa senkronizasyon hatası da olamaz.

Aynı desen GitHub tarafında zaten vardı (App private key → kısa ömürlü
installation token); Azure OpenAI da artık aynı modelde.

### Nasıl çalışıyor

`AzureEntraTokenPolicy`, OpenAI SDK'sının istek hattına giriyor ve `Authorization`
başlığını Entra ID token'ıyla değiştiriyor. Püf nokta: API anahtarı da AAD token'ı
da aynı başlıkla gidiyor (`Authorization: Bearer ...`), yani değişen tek şey o
başlığın içeriği.

`Azure.AI.OpenAI` paketi bu işi kutudan yapıyor ama endpoint yolunu kendi kuruyor
ve bizim `.../openai/v1/` biçimindeki adresimizi bozardı — bu yüzden düz OpenAI
SDK'sı korunup yalnızca kimlik katmanı değiştirildi.

**Seçim kuralı** (`LlmServiceFactory`): anahtar verilmişse anahtar, verilmemişse
managed identity. Hangisinin seçildiği başlangıçta loglanıyor.

### Gereken rol

`Cognitive Services OpenAI User`, Azure OpenAI kaynağı kapsamında,
`id-ciagent` kimliğine atanmış durumda.

---

## 2. Installation başına hız sınırı

Varsayılan: **20 iş / saat**, kayan pencere (`CI_AGENT_MAX_JOBS_PER_HOUR`).

Agent'ın maliyeti başkalarının davranışına bağlı: her CI hatası bir LLM çağrısı,
her `/fix` birkaç çağrı artı bir container. Bozuk bir dalı defalarca push eden tek
bir repo, sınır olmadan faturayı sınırsız büyütebilirdi.

İki tasarım kararı:

- **Kayan pencere**, sabit pencere değil — sabit pencerede "sınır sıfırlandı" anı
  olur ve tam o anda gelen yığın sınırı iki katına çıkarabilir.
- **Sınıra takılan iş 202 alır**, 503 değil — 503 GitHub'ı tekrar denemeye iter,
  yani sınırı aşan installation'ı daha da hızlandırırdı.

---

## 3. Uçtan uca izleme

`appi-ciagent` (Application Insights), aynı Log Analytics çalışma alanına bağlı.
`Azure.Monitor.OpenTelemetry.AspNetCore` ile HTTP istekleri, giden bağımlılık
çağrıları (GitHub API, Azure OpenAI, ARM) ve süreleri tek bir işlem altında
toplanıyor.

Opsiyonel: `APPLICATIONINSIGHTS_CONNECTION_STRING` yoksa servis izlemesiz ama
sorunsuz çalışıyor.

---

## 4. Sürüm yayınlama (`release.yml`)

`v*` etiketi → test → `az acr build` → web + job güncelleme → sağlık kontrolü.

Azure'a bağlantı **OIDC / federated credential** ile, yani saklanan bir parola
yok. Alternatif (service principal parolasını GitHub secret'ında tutmak) tam da
1. maddede anlatılan hataya açık olurdu.

Kimlik dar kapsamlı: abonelik geneli `Contributor` verilmedi, yalnızca

| Rol | Kapsam |
|---|---|
| `AcrPush` | ACR |
| `Contributor` | `ci-agent-web` |
| `Container Apps Jobs Contributor` | `ci-agent-fix` |

Kurulum: `./infra/40-setup-release-oidc.sh`

---

## 5. Eski workflow'lar

`ci-agent.yml` ve `ci-agent-fix.yml` (pilot repoda) **devre dışı** bırakıldı —
silinmedi. İkisi de açıkken her olay iki kez işleniyordu: çift LLM ücreti ve
birbirini ezen PR yorumları. Geri açmak tek komut:

```bash
gh api -X PUT repos/<owner>/<repo>/actions/workflows/<id>/enable
```

---

## BİLEREK YAPILMAYAN: kalıcı kuyruk

Planda "kuyruğu Azure Storage Queue'ya taşı" maddesi vardı. **Bilinçli olarak
ertelendi** — eksik değil, karar.

### Neyi kaçırıyoruz

Kuyruk `ci-agent-web` process'inin belleğinde. Webhook'a `202` döndüğümüz an GitHub
teslimatı başarılı sayıp bir daha göndermiyor. Eğer tam o sırada process ölürse
(deploy sırasında ACA eski revizyonu öldürdüğünde) kuyruktaki iş **sessizce
kaybolur** — ne tekrar denenir ne de logda görünür.

### Neden yine de ertelendi

- **Pencere dar:** iş genelde saniyeler içinde işleniyor; kaybın olması için
  deploy'un tam o saniyeye denk gelmesi gerekiyor.
- **Telafisi kolay ve elle:** analiz kaybolursa App'in *Recent Deliveries*
  ekranından **Redeliver**; `/fix` kaybolursa tekrar `/fix` yazmak yeterli.
- **Bedeli sürekli:** yeni Azure kaynağı, yeni bağımlılık, `WorkQueue`'nun
  arayüze çıkarılması, görünürlük zaman aşımı ve poison message gibi yeni hata
  sınıfları.

Yani nadir ve elle telafi edilebilir bir riski, kalıcı karmaşıklıkla kapatmak
olurdu.

### Ne zaman yeniden değerlendirilmeli

- Gerçekten kayıp yaşanırsa (deploy sırasında kaybolan bir teslimat fark edilirse)
- Replika sayısı 1'in üzerine çıkarsa — o zaman bellekteki kuyruk zaten yetmez
- İş süresi uzarsa (kuyrukta bekleyen iş sayısı sürekli 0'dan büyük olursa)
