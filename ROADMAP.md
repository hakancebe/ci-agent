# ROADMAP

Bilinçli olarak ertelenmiş işler. Her biri bir **tetikleyici koşula** bağlı —
"bir gün lazım olur" diye değil, gerçek bir gözlem çıkarsa yapılacak.

## 1. Skorlamalı log seçimi (eski "Katman 2")

`LlmService.TrimLog` şu an kör kesiyor: baştan 1500, sondan 6500 karakter.
Yerine satır bazlı alaka skorlaması (`error`, `Exception`, `FAIL`, `##[error]`,
`at ` stack frame'i, `dosya.cs:42` deseni) + en yüksek skorlu satırların
±20 satır bağlamı. Ayrıca bütçeyi karakterle değil token'la ölçmek: base64
kötü tokenize oluyor, 8000 karakterlik sınır yanıltıcı bir güvence.

**Tetikleyici:** LLM cevaplarında "log yetersiz / confidence: low" oranı gözle
görülür şekilde artarsa, ya da kırpma yüzünden gerçek hatanın prompt'a
girmediği somut bir vaka çıkarsa.

## 2. `ErrorKind` + detector registry (eski "Katman 3")

`ExtractGenericError` şu an if-zinciri; `IErrorDetector` listesine (`Priority` +
`TryDetect`) çevrilirse yeni ekosistem eklemek metot düzenlemek yerine sınıf
eklemek olur. Yanında `ErrorContext.Kind` (TestFailure | CompilerError |
RestoreError | Annotation | Unknown) ve Unknown oranı için telemetri.

**Tetikleyici:** Telemetride Unknown oranı yüksek çıkarsa, ya da .NET dışında
bir ekosistem (npm, pytest, Docker) desteklenmesi gerekirse.

## 3. Blob tespitinde entropi ölçütü

`LogParser.SanitizeLine` sadece uzunluk + boşluk oranına bakıyor. Entropi
(Shannon, eşik ~5.2) ikinci ölçüt olarak denendi ve **çıkarıldı**: ölçülen 7
gürültü vakasının 6'sını (tek parça base64, hex dump, tekrarlı blob, JWT satırı,
minified JS, boşluklu hex) zaten boşluk kuralı yakalıyordu.

Tek başına entropinin yakaladığı vaka: **40 karakterde bir boşlukla sarmalanmış
base64** (entropi 5.86, boşluk %2.1). Bu desen henüz gerçek bir job logunda
gözlemlenmedi.

**Tetikleyici:** Sarmalanmış base64 (veya benzeri "boşluklu ama yüksek
düzensizlikli" içerik) gerçek bir logda görülürse. O zaman elde somut bir örnek
olur ve eşik ona göre kalibre edilir.

## Kapsam dışı bırakılmış küçük notlar

- `ExtractGenericError`, `TrimToTestSummary`, `BuildFilteredTestLog` içindeki
  `Regex.Match(...)` statik çağrılarında `matchTimeout` yok (static alan
  olmadıkları için). Desenleri lazy quantifier içermediğinden `TestFailureRegex`
  kadar riskli değiller, ama korumasızlar.
- `Masker` bir kural timeout'a düşerse sessizce kısmi maskelenmiş içerikle devam
  ediyor. Fail-closed davranış (timeout'ta içeriği tamamen gizle) tartışıldı,
  şimdilik ertelendi.
