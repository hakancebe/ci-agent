using CiAgent.Service;

namespace CiAgent.Tests;

/// <summary>
/// Dedup'ın asıl sebebi: GitHub 2xx alamadığı webhook'u tekrar gönderiyor ve
/// kullanıcı da "Redeliver"a basabiliyor. Dedup olmadan aynı CI hatası için PR'a
/// üst üste aynı yorum düşerdi.
/// </summary>
public class WorkQueueTests
{
    private static AnalysisJob Job(string deliveryId, long runId = 1)
        => new(deliveryId, "hakancebe", "ci-agent-pilot", runId, 999);

    [Fact]
    public void TryEnqueue_AcceptsFirstDelivery()
    {
        var queue = new WorkQueue();

        Assert.Equal(EnqueueResult.Queued, queue.TryEnqueue(Job("delivery-1")));
    }

    [Fact]
    public void TryEnqueue_RejectsSameDeliveryTwice()
    {
        var queue = new WorkQueue();
        queue.TryEnqueue(Job("delivery-1"));

        Assert.Equal(EnqueueResult.Duplicate, queue.TryEnqueue(Job("delivery-1")));
    }

    [Fact]
    public void TryEnqueue_AllowsSameRunWithDifferentDeliveryId()
    {
        // Dedup anahtarı delivery ID, run ID DEĞİL. Aynı run için GitHub yeni bir
        // olay üretirse (ör. re-run) bu ayrı bir iştir ve işlenmeli.
        var queue = new WorkQueue();
        queue.TryEnqueue(Job("delivery-1", runId: 500));

        Assert.Equal(EnqueueResult.Queued, queue.TryEnqueue(Job("delivery-2", runId: 500)));
    }

    [Fact]
    public void TryEnqueue_ReportsFullWhenCapacityReached()
    {
        var queue = new WorkQueue(capacity: 2);

        Assert.Equal(EnqueueResult.Queued, queue.TryEnqueue(Job("d1")));
        Assert.Equal(EnqueueResult.Queued, queue.TryEnqueue(Job("d2")));
        Assert.Equal(EnqueueResult.Full, queue.TryEnqueue(Job("d3")));
    }

    [Fact]
    public void TryEnqueue_FullQueueDoesNotPoisonDedup()
    {
        // Kritik davranış: kuyruk doluyken reddedilen iş "görüldü" diye
        // işaretlenmemeli. Aksi halde GitHub tekrar gönderdiğinde duplicate sayılıp
        // sessizce yutulur ve o CI hatası HİÇ analiz edilmez.
        var queue = new WorkQueue(capacity: 1);
        queue.TryEnqueue(Job("d1"));

        Assert.Equal(EnqueueResult.Full, queue.TryEnqueue(Job("d2")));

        // Kuyruk boşalınca aynı delivery yeniden kabul edilebilmeli.
        _ = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator().MoveNextAsync();

        Assert.Equal(EnqueueResult.Queued, queue.TryEnqueue(Job("d2")));
    }

    [Fact]
    public async Task ReadAllAsync_YieldsEnqueuedJobsInOrder()
    {
        var queue = new WorkQueue();
        queue.TryEnqueue(Job("d1", runId: 1));
        queue.TryEnqueue(Job("d2", runId: 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<long>();

        await foreach (var job in queue.ReadAllAsync(cts.Token))
        {
            received.Add(job.Analysis!.RunId);
            if (received.Count == 2) break;
        }

        Assert.Equal(new long[] { 1, 2 }, received);
    }

    [Fact]
    public void TryEnqueue_PrunesSeenDeliveriesWithoutLosingRecentOnes()
    {
        // Delivery ID seti sınırsız büyüyemez (uzun ömürlü servis). Budama sonrası
        // EN SON eklenen ID'nin hâlâ duplicate sayılması gerekiyor - budama en
        // eskileri atmalı, rastgele değil.
        var queue = new WorkQueue(capacity: 5000, dedupCapacity: 10);

        for (var i = 0; i < 50; i++)
            queue.TryEnqueue(Job($"delivery-{i}"));

        Assert.Equal(EnqueueResult.Duplicate, queue.TryEnqueue(Job("delivery-49")));
    }
}
