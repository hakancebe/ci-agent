using Microsoft.Extensions.Logging;

namespace CiAgent.Cli;

/// <summary>
/// Düz metin ILogger. Microsoft.Extensions.Logging.Console yerine bu var çünkü
/// standart konsol sağlayıcısı her satıra "info: CiAgent.Core.CiAnalysisPipeline[0]"
/// öneki basıyor — GitHub Actions log'unda insan tarafından okunan bir çıktı için
/// bu gürültü. Burada uyarı/hata stderr'e, gerisi stdout'a düz satır olarak gidiyor.
/// </summary>
public sealed class ConsoleLogger : ILogger<CiAgent.Core.CiAnalysisPipeline>
{
    public static ILogger<CiAgent.Core.CiAnalysisPipeline> Create() => new ConsoleLogger();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);

        var (writer, prefix) = logLevel switch
        {
            LogLevel.Error or LogLevel.Critical => (Console.Error, "HATA: "),
            LogLevel.Warning => (Console.Error, "UYARI: "),
            _ => (Console.Out, "")
        };

        writer.WriteLine(prefix + message);

        // Exception'ın tipi ve mesajı teşhis için şart; stack trace CI logunu
        // boğmasın diye yalnızca bu ikisi basılıyor.
        if (exception is not null)
            writer.WriteLine($"       {exception.GetType().Name}: {exception.Message}");
    }
}
