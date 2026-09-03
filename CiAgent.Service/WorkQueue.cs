using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CiAgent.Service;

/// <summary>
/// Kuyruğun taşıdığı iş. İki tipten yalnızca biri dolu olur — analiz ya da /fix.
///
/// Ayrı kuyruklar yerine TEK kuyruk kullanılıyor: worker sıralı çalıştığı için
/// bu, "aynı anda yalnızca bir iş" garantisini bedavaya veriyor. /fix'in PR
/// bazlı serileştirme ihtiyacı da böylece karşılanmış oluyor — aynı dala iki
/// eşzamanlı push imkânsız hale geliyor.
/// </summary>
internal sealed record QueuedWork(AnalysisJob? Analysis = null, FixJob? Fix = null)
{
    public string DeliveryId => Analysis?.DeliveryId ?? Fix!.DeliveryId;

    public override string ToString() => Analysis?.ToString() ?? Fix!.ToString();
}

internal enum EnqueueResult
{
    /// <summary>Kuyruğa alındı.</summary>
    Queued,

    /// <summary>Bu delivery daha önce görüldü — tekrar işlenmedi.</summary>
    Duplicate,

    /// <summary>Kuyruk dolu; iş alınmadı.</summary>
    Full
}

/// <summary>
/// Webhook ile worker arasındaki tampon.
///
/// Neden kuyruk? GitHub webhook'a ~10 saniyede cevap bekliyor; analiz (log indirme +
/// LLM çağrısı) dakikalar sürebiliyor. HTTP handler'ında işi yapmak, GitHub'ın
/// timeout'a düşüp AYNI olayı tekrar göndermesi demek olurdu — yani her analiz
/// birden fazla kez çalışırdı.
///
/// Bu kuyruk BELLEKTE: servis yeniden başlarsa bekleyen işler kaybolur. Bilinçli
/// bir Faz 1 kararı — kalıcılık (Azure Storage Queue) Faz 3'te. Kayıp durumunda
/// sonuç "o CI hatası analiz edilmedi" olur; veri bozulmaz.
/// </summary>
internal sealed class WorkQueue
{
    private readonly Channel<QueuedWork> _channel;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenDeliveries = new();
    private readonly int _dedupCapacity;

    public WorkQueue(int capacity = 100, int dedupCapacity = 1000)
    {
        // Sınırlı kapasite bilinçli: sınırsız kuyruk, LLM'in yetişemediği bir durumda
        // belleği şişirip servisi öldürürdü. Dolduğunda TryWrite false döner, biz de
        // GitHub'a 503 veririz — GitHub olayı tekrar gönderir, yani iş kaybolmaz.
        //
        // FullMode=Wait ŞART, DropWrite DEĞİL. DropWrite'ta TryWrite işi sessizce
        // ATAR ama yine de true döner: servis GitHub'a "kuyruğa aldım" (202) der,
        // iş çöpe gider ve GitHub 2xx aldığı için tekrar göndermez — sessiz veri
        // kaybı. Wait modunda TryWrite bloke OLMAZ, sadece dolu olduğunda false
        // döner; asıl istediğimiz davranış bu.
        _channel = Channel.CreateBounded<QueuedWork>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _dedupCapacity = dedupCapacity;
    }

    /// <summary>
    /// İşi kuyruğa alır. Aynı delivery ID daha önce görüldüyse ikinci kez almaz.
    ///
    /// Dedup neden şart? GitHub, 2xx alamadığı webhook'u tekrar gönderiyor; ayrıca
    /// kullanıcı da Redeliver'a basabiliyor. Dedup olmadan aynı CI hatası için PR'a
    /// birden fazla aynı yorum düşerdi.
    /// </summary>
    public EnqueueResult TryEnqueue(AnalysisJob job) => TryEnqueue(new QueuedWork(Analysis: job));

    public EnqueueResult TryEnqueue(FixJob job) => TryEnqueue(new QueuedWork(Fix: job));

    private EnqueueResult TryEnqueue(QueuedWork work)
    {
        // TryAdd atomik: iki istek aynı delivery ile aynı anda gelse bile yalnızca
        // biri true alır.
        if (!_seenDeliveries.TryAdd(work.DeliveryId, DateTimeOffset.UtcNow))
            return EnqueueResult.Duplicate;

        if (!_channel.Writer.TryWrite(work))
        {
            // Kuyruğa giremediyse "görüldü" kaydını geri alıyoruz: aksi halde GitHub
            // tekrar gönderdiğinde duplicate sayılıp sessizce yutulur, iş de hiç
            // yapılmamış olurdu.
            _seenDeliveries.TryRemove(work.DeliveryId, out _);
            return EnqueueResult.Full;
        }

        PruneSeenDeliveries();
        return EnqueueResult.Queued;
    }

    public IAsyncEnumerable<QueuedWork> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Delivery ID seti sınırsız büyümesin diye en eskileri atar. GitHub'ın tekrar
    /// gönderme penceresi saatler mertebesinde; binlik bir tampon o pencereyi
    /// fazlasıyla kapsıyor.
    /// </summary>
    private void PruneSeenDeliveries()
    {
        if (_seenDeliveries.Count <= _dedupCapacity)
            return;

        var toRemove = _seenDeliveries
            .OrderBy(kv => kv.Value)
            .Take(_seenDeliveries.Count - _dedupCapacity / 2)
            .Select(kv => kv.Key);

        foreach (var key in toRemove)
            _seenDeliveries.TryRemove(key, out _);
    }
}
