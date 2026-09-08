using CiAgent.Service;

namespace CiAgent.Tests;

/// <summary>
/// Hız sınırı, agent'ın maliyetini başkalarının davranışından koruyan tek katman:
/// her CI hatası bir LLM çağrısı, her /fix birkaç çağrı artı bir container. Bozuk
/// bir dalı defalarca push eden tek bir repo, sınır olmadan faturayı sınırsız
/// büyütebilir.
///
/// Saat dışarıdan veriliyor: gerçek zamanla test etmek ya saatlerce beklemek ya da
/// sınırı anlamsız derecede küçültmek demekti.
/// </summary>
public class InstallationLimiterTests
{
    private DateTimeOffset _now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private InstallationLimiter Build(int max = 3, int windowMinutes = 60)
        => new(max, TimeSpan.FromMinutes(windowMinutes), () => _now);

    [Fact]
    public void TryAcquire_AllowsUpToLimit()
    {
        var limiter = Build(max: 3);

        Assert.True(limiter.TryAcquire(1).Allowed);
        Assert.True(limiter.TryAcquire(1).Allowed);
        Assert.True(limiter.TryAcquire(1).Allowed);
    }

    [Fact]
    public void TryAcquire_DeniesBeyondLimit()
    {
        var limiter = Build(max: 3);
        for (var i = 0; i < 3; i++) limiter.TryAcquire(1);

        var decision = limiter.TryAcquire(1);

        Assert.False(decision.Allowed);
        Assert.Contains("sınır aşıldı", decision.Reason);
    }

    [Fact]
    public void TryAcquire_LimitsAreIndependentPerInstallation()
    {
        // Bir installation'ın sınırı diğerlerini ETKİLEMEMELİ; aksi halde çok
        // kullanan tek bir repo, herkesi birden kilitlerdi.
        var limiter = Build(max: 2);
        limiter.TryAcquire(1);
        limiter.TryAcquire(1);

        Assert.False(limiter.TryAcquire(1).Allowed);
        Assert.True(limiter.TryAcquire(2).Allowed);
    }

    [Fact]
    public void TryAcquire_RecoversAsWindowSlides()
    {
        var limiter = Build(max: 2, windowMinutes: 60);
        limiter.TryAcquire(1);
        limiter.TryAcquire(1);
        Assert.False(limiter.TryAcquire(1).Allowed);

        // Pencere kayınca en eski kayıt düşüyor ve yer açılıyor.
        _now = _now.AddMinutes(61);

        Assert.True(limiter.TryAcquire(1).Allowed);
    }

    [Fact]
    public void TryAcquire_SlidingWindowHasNoResetBurst()
    {
        // Sabit pencerede, sınırın "sıfırlandığı" anda gelen yığın sınırı iki
        // katına çıkarabilirdi. Kayan pencerede böyle bir an yok: 30 dk sonra
        // hâlâ eski kayıtlar penceredeyken yeni iş kabul edilmemeli.
        var limiter = Build(max: 2, windowMinutes: 60);
        limiter.TryAcquire(1);
        limiter.TryAcquire(1);

        _now = _now.AddMinutes(30);

        Assert.False(limiter.TryAcquire(1).Allowed);
    }

    [Fact]
    public void TryAcquire_DeniedRequestsDoNotFillTheWindow()
    {
        // Reddedilen istek KAYDEDİLMEMELİ; kaydedilseydi sınıra takılan bir
        // installation, denedikçe kilidini uzatırdı.
        var limiter = Build(max: 2, windowMinutes: 60);
        limiter.TryAcquire(1);
        limiter.TryAcquire(1);

        for (var i = 0; i < 10; i++) limiter.TryAcquire(1);   // hepsi reddedilir

        // 61 dakika sonra ilk iki kayıt düşer; reddedilenler eklenmemişse yer açılır.
        _now = _now.AddMinutes(61);

        Assert.True(limiter.TryAcquire(1).Allowed);
    }

    [Fact]
    public void Reason_TellsWhenToRetry()
    {
        // Mesaj "ne zaman tekrar denenebilir" demezse, sınıra takılan kullanıcı
        // körlemesine bekler.
        var limiter = Build(max: 1, windowMinutes: 60);
        limiter.TryAcquire(1);
        _now = _now.AddMinutes(15);

        var decision = limiter.TryAcquire(1);

        Assert.False(decision.Allowed);
        Assert.Contains("45 dk sonra", decision.Reason);
    }

    [Fact]
    public void CurrentCount_ReflectsWindow()
    {
        var limiter = Build(max: 5, windowMinutes: 60);
        limiter.TryAcquire(1);
        limiter.TryAcquire(1);

        Assert.Equal(2, limiter.CurrentCount(1));

        _now = _now.AddMinutes(61);
        Assert.Equal(0, limiter.CurrentCount(1));
    }

    [Fact]
    public void CurrentCount_UnknownInstallationIsZero()
    {
        Assert.Equal(0, Build().CurrentCount(999));
    }
}
