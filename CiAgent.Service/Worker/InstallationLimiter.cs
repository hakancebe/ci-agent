using System.Collections.Concurrent;

namespace CiAgent.Service;

/// <summary>Bir işin sınıra takılıp takılmadığı ve takıldıysa sebebi.</summary>
internal sealed record LimitDecision(bool Allowed, string? Reason)
{
    public static readonly LimitDecision Allow = new(true, null);
    public static LimitDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Installation (yani kurulu olduğu hesap/organizasyon) başına iş hızını sınırlar.
///
/// Neden gerekli? Agent'ın maliyeti başkalarının davranışına bağlı: her CI hatası
/// bir LLM çağrısı, her /fix birkaç çağrı artı bir container. Tek bir repo
/// çıldırırsa (bozuk bir dal defalarca push edilir, bir döngü CI'ı sürekli
/// patlatır) fatura sınırsız büyür ve o installation diğerlerinin de kotasını yer.
///
/// Kayan pencere (sliding window) kullanılıyor, sabit pencere değil: sabit pencerede
/// bir "sınır sıfırlanma" anı olur ve tam o anda gelen yığın sınırı iki katına
/// çıkarabilir. Kayan pencerede son N dakikaya bakıldığı için böyle bir boşluk yok.
///
/// Sınıra takılan iş SESSİZCE atılıyor (kuyruğa alınmıyor, GitHub'a 202 dönüyor):
/// 503 dönmek GitHub'ı tekrar denemeye iter ve sınırı aşan installation'ı daha da
/// hızlandırırdı — tam tersi etki.
/// </summary>
internal sealed class InstallationLimiter
{
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _now;

    // installation id → o installation'ın son çalıştırma zamanları.
    private readonly ConcurrentDictionary<long, List<DateTimeOffset>> _history = new();

    /// <param name="maxPerWindow">Pencere başına en fazla iş sayısı.</param>
    /// <param name="window">Pencere uzunluğu.</param>
    /// <param name="now">Test edilebilirlik için saat kaynağı.</param>
    public InstallationLimiter(
        int maxPerWindow = 20,
        TimeSpan? window = null,
        Func<DateTimeOffset>? now = null)
    {
        _maxPerWindow = maxPerWindow;
        _window = window ?? TimeSpan.FromHours(1);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// İşi kaydeder ve izin verilip verilmediğini döner. İzin verilmediyse
    /// KAYDEDİLMEZ — aksi halde sınıra takılan istekler pencereyi doldurup
    /// installation'ı gereğinden uzun süre kilitli tutardı.
    /// </summary>
    public LimitDecision TryAcquire(long installationId)
    {
        var now = _now();
        var cutoff = now - _window;

        var timestamps = _history.GetOrAdd(installationId, _ => new List<DateTimeOffset>());

        lock (timestamps)
        {
            // Pencere dışında kalanlar atılıyor; liste böylece sınırlı kalıyor.
            timestamps.RemoveAll(t => t < cutoff);

            if (timestamps.Count >= _maxPerWindow)
            {
                var oldest = timestamps[0];
                var retryAfter = oldest + _window - now;

                return LimitDecision.Deny(
                    $"installation {installationId} için sınır aşıldı "
                    + $"({_maxPerWindow} iş / {_window.TotalMinutes:0} dk); "
                    + $"yaklaşık {retryAfter.TotalMinutes:0} dk sonra yeniden denenebilir");
            }

            timestamps.Add(now);
            return LimitDecision.Allow;
        }
    }

    /// <summary>Teşhis için: bir installation'ın penceredeki mevcut iş sayısı.</summary>
    public int CurrentCount(long installationId)
    {
        if (!_history.TryGetValue(installationId, out var timestamps))
            return 0;

        var cutoff = _now() - _window;
        lock (timestamps)
            return timestamps.Count(t => t >= cutoff);
    }
}
